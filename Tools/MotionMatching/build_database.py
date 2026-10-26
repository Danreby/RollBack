"""Monta o banco de movimento da locomoção a partir dos .FBX da ExplosiveLLC.

Cada clipe de locomoção (parado, andando, correndo; desarmado e com a espada de duas mãos) vira
um ciclo fechado a 60 fps. Os ciclos de andar/correr são reamostrados em várias escalas de tempo,
o que cobre uma faixa contínua de velocidades. Para cada frame são guardados:

  * pose: rotações locais de todos os ossos e a posição do quadril (B_Pelvis) em relação à raiz;
  * raiz de simulação (osso 0): projeção no chão do deslocamento do corpo, sempre virada para +Z;
  * velocidades lineares/angulares (diferenças finitas, respeitando o loop);
  * contato de cada pé com o chão;
  * features do Motion Matching (Holden et al.): posição e velocidade dos pés, velocidade do
    quadril, trajetória futura (posições e direções em +20/+40/+60 frames) e a postura
    (desarmado/armado).

Saída: build/database.npz (usado pelo treino do LMM e pela exportação para a Unity).
"""
import os
import numpy as np
import fbx
import quat

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, '..', '..'))
PACK = os.path.join(PROJECT, 'Assets', 'ExplosiveLLC', 'RPG Character Mecanim Animation Pack FREE')
MODEL = os.path.join(PACK, 'Models', 'Characters', 'RPG-Character.FBX')
ANIMS = os.path.join(PACK, 'Animations')
OUT = os.path.join(HERE, 'build', 'database.npz')

FPS = 60
SOURCE_FPS = 30

# (arquivo, postura 0=desarmado 1=armado, tipo, escalas de tempo)
WALK_SCALES = np.round(np.arange(0.30, 1.101, 0.05), 3)
RUN_SCALES = np.round(np.arange(0.75, 1.451, 0.05), 3)
CLIPS = [
    ('Unarmed/RPG-Character@Unarmed-Idle', 0, 'idle', [1.0]),
    # Avanço com a guarda alta: é o "andar" do lutador desarmado
    ('Unarmed/RPG-Character@Unarmed-Strafe-Forward', 0, 'walk', WALK_SCALES),
    ('Unarmed/RPG-Character@Unarmed-Run-Forward', 0, 'run', RUN_SCALES),
    ('2Hand-Sword/RPG-Character@2Hand-Sword-Idle', 1, 'idle', [1.0]),
    ('2Hand-Sword/RPG-Character@2Hand-Sword-Walk', 1, 'walk', WALK_SCALES),
    ('2Hand-Sword/RPG-Character@2Hand-Sword-Run-Forward', 1, 'run', RUN_SCALES),
]
KINDS = {'idle': 0, 'walk': 1, 'run': 2}

FEET = ('B_L_Foot', 'B_R_Foot')
TOES = ('B_L_Toe0', 'B_R_Toe0')
HIPS = 'B_Pelvis'
TRAJ_FRAMES = (20, 40, 60)

# Pesos das features (Holden): pés 0.75, velocidades 1, trajetória 1 / 1.5. A postura tem peso alto:
# trocar de desarmado para armado nunca deve sair "mais barato" que manter a postura certa.
FEATURE_GROUPS = [
    ('left_foot_position', 3, 0.75),
    ('right_foot_position', 3, 0.75),
    ('left_foot_velocity', 3, 1.0),
    ('right_foot_velocity', 3, 1.0),
    ('hip_velocity', 3, 1.0),
    ('trajectory_positions', 6, 1.0),
    ('trajectory_directions', 6, 1.5),
    ('stance', 1, 6.0),
    # Os ciclos têm velocidade constante: a velocidade atual da raiz diz qual escala de tempo
    # combina com o personagem agora (a trajetória futura sozinha puxa para a média do próximo 1 s)
    ('root_velocity', 2, 1.0),
    # Parado x em movimento: sem animações de parada no pacote, isto decide quando entrar no idle
    ('moving', 1, 3.0),
]

# Contato com o chão: ponta do pé baixa e quase parada no mundo
CONTACT_HEIGHT = 0.07
CONTACT_SPEED = 0.9


def skeleton(scene):
    """Ossos na ordem de hierarquia (pais antes dos filhos). O nó Motion vira a raiz de simulação."""
    kids = {}
    for i, p in scene.parent.items():
        kids.setdefault(p, []).append(i)
    motion = scene.by_name['Motion']
    names, parents = [], []

    def visit(i, parent):
        names.append(scene.name[i])
        parents.append(parent)
        me = len(names) - 1
        for c in sorted(kids.get(i, []), key=lambda c: scene.name[c]):
            visit(c, me)

    visit(motion, -1)
    names[0] = 'Root'
    return names, np.array(parents)


def sample_keys(model, clip, names, parents):
    """Pose em cada key de 30 fps: rotações locais (espaço Unity) e posições globais dos ossos."""
    nkeys = int(round(clip.duration() * SOURCE_FPS)) + 1
    lrot = np.zeros((nkeys, len(names), 4))
    lpos = np.zeros((nkeys, len(names), 3))
    gpos = np.zeros((nkeys, len(names), 3))
    for k in range(nkeys):
        W = model.world(k / SOURCE_FPS, clip)
        U = {n: fbx.to_unity(W['Motion' if n == 'Root' else n]) for n in names}
        for b, n in enumerate(names):
            M = U[n] if parents[b] < 0 else np.linalg.inv(U[names[parents[b]]]) @ U[n]
            lrot[k, b] = quat.from_matrix(M[:3, :3])
            lpos[k, b] = M[:3, 3]
            gpos[k, b] = U[n][:3, 3]
    return lrot, lpos, gpos


def make_cycle(lrot, lpos, gpos, names, kind):
    """Fecha o ciclo: raiz andando em linha reta com o corpo e correção linear do erro de loop."""
    P = len(lrot) - 1                       # período em keys (a última key repete a primeira)
    hips = names.index(HIPS)
    start = gpos[0, 0].copy()
    start[1] = 0.0
    # Deslocamento real do corpo (o nó Motion de alguns clipes anda mais que o corpo)
    travel = 0.0 if kind == 'idle' else gpos[P, hips, 2] - gpos[0, hips, 2]
    root = np.repeat(start[None], P + 1, axis=0)
    root[:, 2] += travel * np.arange(P + 1) / P

    # Rotação da raiz: identidade (+Z). Pose do quadril relativa à raiz.
    lrot = lrot.copy()
    lpos = lpos.copy()
    assert np.allclose(np.abs(lrot[:, 0, 3]), 1.0, atol=1e-4), 'o nó Motion deveria estar sempre virado para +Z'
    lrot[:, 0] = [0, 0, 0, 1]
    lpos[:, 0] = root
    lpos[:, hips] = gpos[:, hips] - root

    # Continuidade de hemisfério e correção do loop
    for k in range(1, P + 1):
        flip = np.sum(lrot[k] * lrot[k - 1], axis=-1) < 0
        lrot[k][flip] *= -1
    w = (np.arange(P + 1) / P)[:, None, None]
    err_rot = quat.to_scaled_angle_axis(quat.abs(quat.mul_inv(lrot[P], lrot[0])))   # [bones,3]
    lrot = quat.mul(quat.from_scaled_angle_axis(-err_rot[None] * w), lrot)
    err_pos = lpos[P, 1:] - lpos[0, 1:]
    lpos[:, 1:] -= err_pos[None] * w
    return lrot, lpos, travel, float(np.degrees(np.linalg.norm(err_rot, axis=-1).max())), float(np.abs(err_pos).max())


def resample(lrot, lpos, travel, scale):
    """Ciclo de P keys (30 fps) -> N frames a 60 fps tocando 'scale' vezes mais rápido."""
    P = len(lrot) - 1
    N = max(2, int(round(P * FPS / SOURCE_FPS / scale)))
    actual = P * FPS / SOURCE_FPS / N
    u = np.arange(N) * P / N
    k0 = np.floor(u).astype(int)
    a = (u - k0)[:, None, None]
    rot = quat.slerp(lrot[k0], lrot[k0 + 1], a)
    pos = lpos[k0] * (1 - a) + lpos[k0 + 1] * a
    # raiz: reta com velocidade constante
    pos[:, 0] = lpos[0, 0]
    pos[:, 0, 2] += travel * np.arange(N) / N
    return quat.normalize(rot), pos, actual, travel * FPS / N


def periodic_velocities(rot, pos, cycle_travel):
    """Velocidades por diferença central com wrap-around (o ciclo continua depois do último frame)."""
    N = len(rot)
    nxt = np.roll(np.arange(N), -1)
    prv = np.roll(np.arange(N), 1)
    pos_next = pos[nxt].copy()
    pos_prev = pos[prv].copy()
    pos_next[-1, 0, 2] += cycle_travel           # raiz depois do fim do ciclo
    pos_prev[0, 0, 2] -= cycle_travel
    vel = (pos_next - pos_prev) * FPS / 2
    d1 = quat.to_scaled_angle_axis(quat.abs(quat.mul_inv(rot[nxt], rot)))
    d0 = quat.to_scaled_angle_axis(quat.abs(quat.mul_inv(rot, rot[prv])))
    ang = (d1 + d0) * FPS / 2
    return vel, ang


def build():
    model = fbx.Scene(MODEL)
    names, parents = skeleton(model)
    nb = len(names)
    hips = names.index(HIPS)
    feet = [names.index(n) for n in FEET]
    toes = [names.index(n) for n in TOES]

    R, Pp, V, A, C = [], [], [], [], []
    starts, stops, stance, kinds, scales, speeds, clip_ids, travels = [], [], [], [], [], [], [], []
    # Translação local dos ossos no bind do modelo: a Unity (humanoide) mantém o comprimento dos
    # ossos do avatar e só anima a posição do quadril
    W = model.world()
    U = {n: fbx.to_unity(W['Motion' if n == 'Root' else n]) for n in names}
    bind_lpos = np.array([U[n][:3, 3] if parents[b] < 0 else (np.linalg.inv(U[names[parents[b]]]) @ U[n])[:3, 3]
                          for b, n in enumerate(names)])
    report = []
    for ci, (path, st, kind, scale_list) in enumerate(CLIPS):
        clip = fbx.Scene(os.path.join(ANIMS, path + '.FBX'))
        lrot, lpos, gpos = sample_keys(model, clip, names, parents)
        lrot, lpos, travel, erot, epos = make_cycle(lrot, lpos, gpos, names, kind)
        report.append(f"{os.path.basename(path):40s} keys={len(lrot) - 1:3d} desloc/ciclo={travel:.3f} m "
                      f"erro de loop: {erot:.2f} graus, {epos * 100:.2f} cm")
        for s in scale_list:
            rot, pos, actual, speed = resample(lrot, lpos, travel, s)
            vel, ang = periodic_velocities(rot, pos, travel)
            starts.append(sum(len(r) for r in R))
            R.append(rot); Pp.append(pos); V.append(vel); A.append(ang)
            stops.append(starts[-1] + len(rot))
            stance.append(st); kinds.append(KINDS[kind]); scales.append(actual)
            speeds.append(speed); clip_ids.append(ci); travels.append(travel)

    rot = np.concatenate(R); pos = np.concatenate(Pp); vel = np.concatenate(V); ang = np.concatenate(A)
    nframes = len(rot)
    starts = np.array(starts); stops = np.array(stops)
    range_of = np.zeros(nframes, int)
    for r, (a, b) in enumerate(zip(starts, stops)):
        range_of[a:b] = r

    # Constantes: só o quadril (e a raiz) se deslocam; os demais ossos mantêm o comprimento do bind
    drift = np.abs(pos[:, 2:] - bind_lpos[None, 2:]).max()
    report.append(f"maior variação de translação local fora quadril/raiz: {drift * 1000:.2f} mm")
    pos[:, 2:] = bind_lpos[None, 2:]

    grot, gpos, gvel, gang = quat.fk_vel(rot, pos, vel, ang, parents)

    # ---- Contatos ----
    toe_h = gpos[:, toes, 1]
    toe_v = np.linalg.norm(gvel[:, toes][:, :, [0, 2]], axis=-1)
    contacts = (toe_h < CONTACT_HEIGHT) & (toe_v < CONTACT_SPEED)
    # Remove contatos/levantadas de 1 frame (ruído)
    for r in range(len(starts)):
        a, b = starts[r], stops[r]
        for f in range(2):
            c = contacts[a:b, f].copy()
            n = len(c)
            for i in range(n):
                if c[(i - 1) % n] == c[(i + 1) % n] != c[i]:
                    contacts[a + i, f] = c[(i - 1) % n]

    # ---- Features ----
    root_rot = grot[:, 0]
    root_pos = gpos[:, 0]

    def char_space_pos(b):
        return quat.inv_mul_vec(root_rot, gpos[:, b] - root_pos)

    def char_space_vel(b):
        return quat.inv_mul_vec(root_rot, gvel[:, b])

    traj_pos, traj_dir = [], []
    for k in TRAJ_FRAMES:
        fut_pos = np.zeros((nframes, 3)); fut_rot = np.zeros((nframes, 4))
        for i in range(nframes):
            r = range_of[i]
            a, b = starts[r], stops[r]
            n = b - a
            j = i - a + k
            wraps, jj = divmod(j, n)
            fut_pos[i] = gpos[a + jj, 0] + wraps * np.array([0.0, 0.0, travels[r]])
            fut_rot[i] = grot[a + jj, 0]
        rel = quat.inv_mul_vec(root_rot, fut_pos - root_pos)
        d = quat.inv_mul_vec(root_rot, quat.mul_vec(fut_rot, np.array([0.0, 0.0, 1.0])))
        traj_pos.append(rel[:, [0, 2]]); traj_dir.append(d[:, [0, 2]])

    raw = np.concatenate([
        char_space_pos(feet[0]), char_space_pos(feet[1]),
        char_space_vel(feet[0]), char_space_vel(feet[1]),
        char_space_vel(hips),
        np.concatenate(traj_pos, axis=-1),
        np.concatenate(traj_dir, axis=-1),
        np.array(stance)[range_of][:, None].astype(np.float64),
        quat.inv_mul_vec(root_rot, gvel[:, 0])[:, [0, 2]],
        (np.array(kinds)[range_of] != KINDS['idle'])[:, None].astype(np.float64),
    ], axis=-1)

    offset = raw.mean(axis=0)
    scale = np.ones(raw.shape[1])
    col = 0
    for name, size, weight in FEATURE_GROUPS:
        std = raw[:, col:col + size].std(axis=0).mean()
        if std < 1e-4:
            # grupo constante no banco (ex.: direção da trajetória, todos os ciclos são retos):
            # usa uma escala típica para o grupo continuar contando nas consultas
            std = {'trajectory_directions': 0.5}.get(name, 1.0)
        scale[col:col + size] = std / weight
        col += size
    features = (raw - offset) / scale

    next_frame = np.arange(1, nframes + 1)
    next_frame[stops - 1] = starts                 # ciclos: o frame depois do último é o primeiro

    np.savez(OUT,
             bone_names=np.array(names), bone_parents=parents,
             bone_positions=pos.astype(np.float32), bone_rotations=rot.astype(np.float32),
             bone_velocities=vel.astype(np.float32), bone_angular_velocities=ang.astype(np.float32),
             contact_states=contacts, range_starts=starts, range_stops=stops,
             range_stance=np.array(stance), range_kind=np.array(kinds), range_scale=np.array(scales),
             range_speed=np.array(speeds), range_clip=np.array(clip_ids), range_travel=np.array(travels),
             clip_names=np.array([c[0] for c in CLIPS]), next_frame=next_frame,
             features=features.astype(np.float32), features_raw=raw.astype(np.float32),
             features_offset=offset.astype(np.float32), features_scale=scale.astype(np.float32),
             feature_groups=np.array([g[0] for g in FEATURE_GROUPS]),
             feature_sizes=np.array([g[1] for g in FEATURE_GROUPS]),
             feature_weights=np.array([g[2] for g in FEATURE_GROUPS]),
             bind_local_positions=bind_lpos.astype(np.float32))
    report.append(f"{nframes} frames, {len(starts)} ciclos, {nb} ossos, {features.shape[1]} features -> {OUT}")
    return report


if __name__ == '__main__':
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    for line in build():
        print(line)
