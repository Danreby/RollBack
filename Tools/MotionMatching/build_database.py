"""Monta o banco de movimento da locomoção a partir dos .FBX da ExplosiveLLC e da Quaternius.

Cada clipe de locomoção (parado, andando, correndo, disparando e andando de lado; desarmado, com a
espada de duas mãos e com armas de uma mão) vira um ciclo fechado a 60 fps. A disparada é a
"Sprint_Loop" da Universal Animation Library (Quaternius, CC0, convertida por
Tools/AnimationImport/import_quaternius.py): tronco inclinado, passadas longas e braços bombeando. Os ciclos de andar/correr são reamostrados em várias escalas de tempo,
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

QUATERNIUS = os.path.join(PROJECT, 'Assets', 'Animations', 'Quaternius')

# Posturas de locomoção: 0 = desarmado (guarda de lutador), 1 = espada de duas mãos,
# 2 = arma de uma mão (adagas e espadão, com as animações da Quaternius)
STANCES = 3
# Escalas de tempo de cada ciclo (o ajuste contínuo do ritmo em jogo cobre o intervalo entre elas)
WALK_SCALES = np.round(np.arange(0.30, 1.101, 0.1), 3)
RUN_SCALES = np.round(np.arange(0.75, 1.451, 0.1), 3)
SPRINT_SCALES = [0.8, 0.95, 1.1]
STRAFE_SCALES = [0.6, 0.8, 1.0, 1.2]
ARMS = ('B_L_Clavicle', 'B_R_Clavicle')            # subárvores trocadas nas animações em camadas


def E(name):
    """Clipe da ExplosiveLLC."""
    folder = name.split('-')[0] if not name.startswith('2Hand') else '2Hand-Sword'
    return os.path.join(ANIMS, folder, f'RPG-Character@{name}.FBX')


def Q(name):
    """Clipe da Quaternius já convertido para o esqueleto do RPG-Character."""
    return os.path.join(QUATERNIUS, f'{name}.fbx')


# Fonte: caminho de um FBX, ou ('layer', base, braços): pernas/quadril/tronco da base e os
# braços (clavículas para baixo) de outro clipe, no mesmo ritmo.
CLIPS = []
for stance, prefix in ((0, 'Unarmed'), (1, '2Hand-Sword')):
    CLIPS += [
        (E(f'{prefix}-Idle'), stance, 'idle', [1.0]),
        (E(f'{prefix}-Run-Forward'), stance, 'run', RUN_SCALES),
        (E(f'{prefix}-Strafe-Left'), stance, 'strafe', STRAFE_SCALES),
        (E(f'{prefix}-Strafe-Right'), stance, 'strafe', STRAFE_SCALES),
        (E(f'{prefix}-Strafe-Backward'), stance, 'strafe', STRAFE_SCALES),
    ] + [(E(f'{prefix}-Run-{d}'), stance, 'strafe', STRAFE_SCALES)
         for d in ('Left', 'Right', 'Backward', 'Forward-Left', 'Forward-Right', 'Backward-Left', 'Backward-Right')]
CLIPS += [
    # Avanço com a guarda alta: é o "andar" do lutador desarmado
    (E('Unarmed-Strafe-Forward'), 0, 'walk', WALK_SCALES),
    (Q('Q-Sprint'), 0, 'sprint', SPRINT_SCALES),
    (E('2Hand-Sword-Walk'), 1, 'walk', WALK_SCALES),
    # disparada com a espada de duas mãos: pernas e tronco do sprint, braços segurando a espada
    (('layer', Q('Q-Sprint'), E('2Hand-Sword-Run-Forward')), 1, 'sprint', SPRINT_SCALES),
    (Q('Q-Sword-Idle'), 2, 'idle', [1.0]),
    (Q('Q-Walk'), 2, 'walk', np.round(np.arange(0.8, 1.81, 0.2), 3)),
    (Q('Q-Jog'), 2, 'run', np.round(np.arange(0.55, 1.051, 0.1), 3)),
    (Q('Q-Sprint'), 2, 'sprint', SPRINT_SCALES),
    (Q('Q-Walk-L'), 2, 'strafe', [1.0, 1.5, 2.0]),
    (Q('Q-Walk-R'), 2, 'strafe', [1.0, 1.5, 2.0]),
    (Q('Q-Walk-Bwd'), 2, 'strafe', [1.0, 1.5, 2.0]),
] + [(('layer', E(f'Unarmed-Run-{d}'), Q('Q-Sword-Idle')), 2, 'strafe', STRAFE_SCALES)
     for d in ('Left', 'Right', 'Backward', 'Forward-Left', 'Forward-Right', 'Backward-Left', 'Backward-Right')]
KINDS = {'idle': 0, 'walk': 1, 'run': 2, 'sprint': 3, 'strafe': 4}

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
    ('stance', STANCES, 6.0),
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


def clip_label(source):
    if isinstance(source, tuple):
        return f"{os.path.basename(source[1])} + braços de {os.path.basename(source[2])}"
    return os.path.basename(source)


def contact_phase(gpos, names):
    """Fração do ciclo em que o pé esquerdo está mais baixo que o direito pela primeira vez."""
    l, r = names.index('B_L_Toe0'), names.index('B_R_Toe0')
    lower = gpos[:-1, l, 1] < gpos[:-1, r, 1]
    start = int(np.argmax(lower & ~np.roll(lower, 1))) if lower.any() and not lower.all() else 0
    return start / (len(gpos) - 1)


def layered_keys(base, arms, names, parents):
    """Corpo da animação 'base' com os braços (clavículas para baixo) da 'arms', no mesmo ritmo."""
    lrot, lpos, gpos = (a.copy() for a in base)
    arot = arms[0]
    P, Pa = len(lrot) - 1, len(arot) - 1
    shift = contact_phase(arms[2], names) - contact_phase(gpos, names)
    overlay = set()
    for i, n in enumerate(names):
        p = i
        while p >= 0:
            if names[p] in ARMS:
                overlay.add(i)
                break
            p = parents[p]
    idx = sorted(overlay)
    for k in range(P + 1):
        u = ((k / P + shift) % 1.0) * Pa
        k0 = int(np.floor(u)) % Pa
        a = u - np.floor(u)
        lrot[k, idx] = quat.slerp(arot[k0, idx], arot[k0 + 1, idx], np.full((len(idx), 1), a))
    return lrot, lpos, gpos


def make_cycle(lrot, lpos, gpos, names, kind):
    """Fecha o ciclo: raiz andando em linha reta com o corpo e correção linear do erro de loop."""
    P = len(lrot) - 1                       # período em keys (a última key repete a primeira)
    hips = names.index(HIPS)
    start = gpos[0, 0].copy()
    start[1] = 0.0
    # Deslocamento real do corpo no ciclo (o nó Motion de alguns clipes anda mais que o corpo);
    # nos strafes ele é de lado ou para trás, com o personagem sempre virado para +Z
    travel = np.zeros(3)
    if kind != 'idle':
        travel[[0, 2]] = gpos[P, hips, [0, 2]] - gpos[0, hips, [0, 2]]
    root = np.repeat(start[None], P + 1, axis=0)
    root += travel[None] * (np.arange(P + 1) / P)[:, None]

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
    pos[:, 0] = lpos[0, 0] + travel[None] * (np.arange(N) / N)[:, None]
    return quat.normalize(rot), pos, actual, np.linalg.norm(travel) * FPS / N


def periodic_velocities(rot, pos, cycle_travel):
    """Velocidades por diferença central com wrap-around (o ciclo continua depois do último frame)."""
    N = len(rot)
    nxt = np.roll(np.arange(N), -1)
    prv = np.roll(np.arange(N), 1)
    pos_next = pos[nxt].copy()
    pos_prev = pos[prv].copy()
    pos_next[-1, 0] += cycle_travel              # raiz depois do fim do ciclo
    pos_prev[0, 0] -= cycle_travel
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
    cache = {}

    def keys_of(path):
        if path not in cache:
            cache[path] = sample_keys(model, fbx.Scene(path), names, parents)
        return cache[path]

    for ci, (source, st, kind, scale_list) in enumerate(CLIPS):
        if isinstance(source, tuple):
            lrot, lpos, gpos = layered_keys(keys_of(source[1]), keys_of(source[2]), names, parents)
        else:
            lrot, lpos, gpos = keys_of(source)
        lrot, lpos, travel, erot, epos = make_cycle(lrot, lpos, gpos, names, kind)
        report.append(f"{clip_label(source):58s} keys={len(lrot) - 1:3d} desloc/ciclo=({travel[0]:5.2f},{travel[2]:5.2f}) m "
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
            fut_pos[i] = gpos[a + jj, 0] + wraps * travels[r]
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
        np.eye(STANCES)[np.array(stance)[range_of]],
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
             clip_names=np.array([clip_label(c[0]) for c in CLIPS]), next_frame=next_frame,
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
