"""Converte animações da Universal Animation Library (Quaternius, CC0) para o esqueleto do
RPG-Character e grava FBX prontos para a Unity (humanoide, avatar copiado do RPG-Character).

Retarget: para cada osso, a rotação global da animação relativa à T-pose da Quaternius é aplicada
sobre a T-pose do RPG-Character (os dois esqueletos estão em T-pose, virados para +Z). A raiz
(movimento no chão) e a altura do quadril são escaladas pela razão entre as alturas do quadril.

O FBX gerado é um clone de um clipe da ExplosiveLLC (mesmos nós, pré-rotações e unidades) com as
curvas trocadas, e o .meta é copiado de um golpe da ExplosiveLLC: a Unity importa igual aos clipes
que já estão no projeto (root motion pelo nó Motion, avatar do RPG-Character).

Uso:  python3 import_quaternius.py            (baixa as fontes se faltarem e gera tudo)
"""
import os
import sys
import re
import copy
import hashlib
import subprocess
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'MotionMatching'))
import fbx      # noqa: E402
import gltf     # noqa: E402
import quat     # noqa: E402

PROJECT = os.path.abspath(os.path.join(HERE, '..', '..'))
SOURCE = os.path.join(HERE, 'source')
PACK = os.path.join(PROJECT, 'Assets', 'ExplosiveLLC', 'RPG Character Mecanim Animation Pack FREE')
MODEL = os.path.join(PACK, 'Models', 'Characters', 'RPG-Character.FBX')
TEMPLATE_FBX = os.path.join(PACK, 'Animations', 'Unarmed', 'RPG-Character@Unarmed-Run-Forward.FBX')
TEMPLATE_META = os.path.join(PACK, 'Animations', '2Hand-Sword', 'RPG-Character@2Hand-Sword-Attack1.FBX.meta')
OUT = os.path.join(PROJECT, 'Assets', 'Animations', 'Quaternius')
FPS = 30

# As bibliotecas são CC0 (domínio público). Cópias públicas no GitHub (os sites oficiais,
# quaternius.com e itch.io, ficam fora do alcance deste ambiente):
DOWNLOADS = {
    'UAL1_Standard.glb': 'https://raw.githubusercontent.com/Arod231/monomachia/master/game/assets/quaternius/animations/UAL1_Standard.glb',
    'UAL2_Source_RM.glb': 'https://raw.githubusercontent.com/Arod231/monomachia/master/game/assets/quaternius/animations/UAL2_Source_RM.glb',
}

# Quaternius -> RPG-Character
BONE_MAP = {
    'pelvis': 'B_Pelvis', 'spine_01': 'B_Spine', 'spine_02': 'B_Spine1', 'spine_03': 'B_Spine2',
    'neck_01': 'B_Neck', 'Head': 'B_Head',
}
for side, s in (('l', 'L'), ('r', 'R')):
    BONE_MAP.update({
        f'clavicle_{side}': f'B_{s}_Clavicle', f'upperarm_{side}': f'B_{s}_UpperArm',
        f'lowerarm_{side}': f'B_{s}_Forearm', f'hand_{side}': f'B_{s}_Hand',
        f'thigh_{side}': f'B_{s}_Thigh', f'calf_{side}': f'B_{s}_Calf', f'foot_{side}': f'B_{s}_Foot',
        f'ball_{side}': f'B_{s}_Toe0',
    })
    for finger, k in (('thumb', 0), ('index', 1), ('middle', 2), ('ring', 3), ('pinky', 4)):
        BONE_MAP[f'{finger}_01_{side}'] = f'B_{s}_Finger{k}'
        BONE_MAP[f'{finger}_02_{side}'] = f'B_{s}_Finger{k}1'
        BONE_MAP[f'{finger}_03_{side}'] = f'B_{s}_Finger{k}2'

# Clipes "no ar" da Quaternius sobem o corpo inteiro (o personagem salta metros dentro do clipe).
# No jogo quem sobe/desce é a cápsula (física), então a subida vira movimento vertical da raiz
# ('above': tudo acima da altura de pé; o código decide se segue) ou é descontada ('offset').
AIR_OFFSET = 2.44

# (arquivo, clipe da Quaternius, nome na Unity, loop, root motion no chão[, vertical])
CLIPS = [
    # locomoção (entra também no banco do Motion Matching). Os loops da UAL1 são "no lugar":
    # a velocidade vem do pé apoiado ('inplace') e vira deslocamento do nó Motion.
    ('UAL1_Standard.glb', 'Sprint_Loop', 'Q-Sprint', True, False, ('inplace', None)),
    ('UAL1_Standard.glb', 'Jog_Fwd_Loop', 'Q-Jog', True, False, ('inplace', None)),
    ('UAL1_Standard.glb', 'Sword_Idle', 'Q-Sword-Idle', True, False),
    ('UAL2_Source_RM.glb', 'Walk_Fwd_Loop', 'Q-Walk', True, True),
    ('UAL2_Source_RM.glb', 'Walk_Bwd_Loop', 'Q-Walk-Bwd', True, True),
    ('UAL2_Source_RM.glb', 'Walk_L_Loop', 'Q-Walk-L', True, True),
    ('UAL2_Source_RM.glb', 'Walk_R_Loop', 'Q-Walk-R', True, True),
    # espada / adagas / espadão: cadeias leve, normal e pesada (golpe + recuperação)
    *[('UAL2_Source_RM.glb', f'Sword_{w}_{k}{r}', f'Q-Sword-{w}-{k}{r.replace("_", "-")}', False, True)
      for w, keys in (('Light', 'ABCD'), ('Regular', 'ABC'), ('Heavy', 'ABCD'))
      for k in keys for r in ('', '_Rec') if not (w == 'Light' and k == 'D' and r) and not (w == 'Heavy' and k == 'D' and r)
      and not (w == 'Regular' and k == 'C' and r)],
    ('UAL2_Source_RM.glb', 'Sword_Dash', 'Q-Sword-Dash', False, True),
    ('UAL2_Source_RM.glb', 'Sword_UpperCut', 'Q-Sword-UpperCut', False, True, ('above', 1.27)),
    ('UAL2_Source_RM.glb', 'Sword_GroundPound', 'Q-Sword-GroundPound', False, True, ('above', 1.27)),
    ('UAL2_Source_RM.glb', 'Sword_Aerial_A', 'Q-Sword-Aerial-A', False, True, ('offset', AIR_OFFSET)),
    ('UAL2_Source_RM.glb', 'Sword_Aerial_B', 'Q-Sword-Aerial-B', False, True, ('offset', AIR_OFFSET)),
    ('UAL2_Source_RM.glb', 'Sword_Aerial_Combo_Loop', 'Q-Sword-Aerial-Spin', True, True),
    ('UAL2_Source_RM.glb', 'Sword_Aerial_Idle_Loop', 'Q-Sword-Aerial-Idle', True, True, ('offset', AIR_OFFSET)),
    # mãos livres
    ('UAL2_Source_RM.glb', 'Melee_Hook', 'Q-Melee-Hook', False, True),
    ('UAL2_Source_RM.glb', 'Melee_Knee', 'Q-Melee-Knee', False, True),
    ('UAL2_Source_RM.glb', 'Melee_Uppercut', 'Q-Melee-Uppercut', False, True),
    ('UAL2_Source_RM.glb', 'DoubleJump', 'Q-DoubleJump', False, True),
    # reações dos bonecos (lançados, malabarismo no ar, queda e levantar)
    ('UAL2_Source_RM.glb', 'LiftAir', 'Q-React-Launch', False, True, ('above', 1.28)),
    ('UAL2_Source_RM.glb', 'LiftAir_Idle_Loop', 'Q-React-Air-Idle', True, True),
    ('UAL2_Source_RM.glb', 'LiftAir_Hit_L', 'Q-React-Air-Hit-L', False, True),
    ('UAL2_Source_RM.glb', 'LiftAir_Hit_R', 'Q-React-Air-Hit-R', False, True),
    ('UAL2_Source_RM.glb', 'LiftAir_Fall', 'Q-React-Air-Fall', False, True, ('above', 1.28)),
    ('UAL2_Source_RM.glb', 'LiftAir_Fall_Air_Loop', 'Q-React-Air-Fall-Loop', True, True),
    ('UAL2_Source_RM.glb', 'LiftAir_Fall_Impact', 'Q-React-Fall-Impact', False, True),
    ('UAL2_Source_RM.glb', 'Hit_Knockback', 'Q-React-Knockback', False, True),
    ('UAL2_Source_RM.glb', 'KipUp', 'Q-React-KipUp', False, True),
]

F = np.diag([-1.0, 1.0, 1.0])   # glTF/FBX (mão direita) <-> Unity (mão esquerda)


def download():
    os.makedirs(SOURCE, exist_ok=True)
    for name, url in DOWNLOADS.items():
        path = os.path.join(SOURCE, name)
        if not os.path.exists(path):
            print('baixando', name)
            subprocess.check_call(['curl', '-sSfL', '-o', path, url])


# ---------------------------------------------------------------- esqueletos

class Source:
    """Animação da Quaternius em espaço Unity (metros), já com FK."""

    def __init__(self, path):
        self.g = gltf.Glb(path)
        g = self.g
        self.order = []
        kids = {}
        for i, p in enumerate(g.parent):
            kids.setdefault(p, []).append(i)

        def visit(i):
            self.order.append(i)
            for c in kids.get(i, []):
                visit(c)
        for r in kids[-1]:
            visit(r)
        self.index = {n: i for i, n in enumerate(g.names)}

    def world(self, clip, times):
        T, R, S = self.g.sample(clip, times)
        n = len(self.g.nodes)
        W = np.zeros((len(times), n, 4, 4))
        for i in self.order:
            M = np.zeros((len(times), 4, 4))
            M[:, :3, :3] = quat.to_matrix(quat.normalize(R[:, i])) * S[:, i][:, None, :]
            M[:, :3, 3] = T[:, i]
            M[:, 3, 3] = 1
            p = self.g.parent[i]
            W[:, i] = M if p < 0 else W[:, p] @ M
        # para o espaço Unity: espelha X, remove escala das rotações
        U = np.zeros_like(W)
        U[..., :3, :3] = F @ W[..., :3, :3] @ F
        U[..., :3, 3] = W[..., :3, 3] @ F.T
        U[..., 3, 3] = 1
        rot = U[..., :3, :3] / np.linalg.norm(U[..., :3, :3], axis=-2, keepdims=True)
        return rot, U[..., :3, 3]


class Target:
    """Bind (T-pose) do RPG-Character em espaço Unity."""

    def __init__(self):
        sc = fbx.Scene(MODEL)
        W = sc.world()
        self.names = ['Motion'] + sorted(n for n in sc.by_name if n.startswith('B_'))
        self.parent = {}
        for n in self.names:
            p = sc.parent[sc.by_name[n]]
            self.parent[n] = sc.name[p] if p is not None else None
        self.world_rot = {n: fbx.to_unity(W[n])[:3, :3] for n in self.names}
        self.world_pos = {n: fbx.to_unity(W[n])[:3, 3] for n in self.names}
        self.local_pos = {}
        for n in self.names:
            p = self.parent[n]
            if p is None:
                self.local_pos[n] = self.world_pos[n]
            else:
                self.local_pos[n] = self.world_rot[p].T @ (self.world_pos[n] - self.world_pos[p])
        # ordem com pais antes dos filhos
        done, order = set(), []
        while len(order) < len(self.names):
            for n in self.names:
                if n not in done and (self.parent[n] is None or self.parent[n] in done):
                    order.append(n)
                    done.add(n)
        self.order = order


def yaw_only(R):
    f = R[..., :, 2].copy()
    f[..., 1] = 0
    f /= np.linalg.norm(f, axis=-1, keepdims=True) + 1e-9
    out = np.zeros(R.shape)
    out[..., 0, 0] = f[..., 2]; out[..., 0, 2] = f[..., 0]
    out[..., 1, 1] = 1
    out[..., 2, 0] = -f[..., 0]; out[..., 2, 2] = f[..., 2]
    return out


def retarget(src, tgt, clip, root_motion, vertical=None):
    """Rotações locais / posições do RPG-Character a 30 fps, em espaço Unity."""
    dur = src.g.duration(clip)
    times = np.arange(0.0, dur + 1e-6, 1.0 / FPS)
    rot, pos = src.world(clip, times)
    rot0, pos0 = src.world('A_TPose', np.array([0.0])) if 'A_TPose' in src.g.animations else (None, None)
    rot0, pos0 = rot0[0], pos0[0]
    si = src.index
    scale = tgt.world_pos['B_Pelvis'][1] / pos0[si['pelvis']][1]

    G = {}
    for u, r in BONE_MAP.items():
        delta = rot[:, si[u]] @ rot0[si[u]].T
        G[r] = delta @ tgt.world_rot[r]

    # raiz no chão (nó Motion): da raiz da Quaternius (clipes com root motion) ou fixa
    n = len(times)
    root_pos = np.zeros((n, 3))
    root_rot = np.repeat(np.eye(3)[None], n, 0)
    if root_motion:
        rp = pos[:, si['root']].copy()
        root_pos[:, [0, 2]] = (rp[:, [0, 2]] - rp[0, [0, 2]]) * scale
        root_rot = yaw_only(rot[:, si['root']] @ rot0[si['root']].T)
    pel = pos[:, si['pelvis']]
    rp = pos[:, si['root']] if root_motion else np.zeros_like(pel)
    pelvis_world = np.zeros((n, 3))
    pelvis_world[:, [0, 2]] = root_pos[:, [0, 2]] + (pel[:, [0, 2]] - rp[:, [0, 2]] - (pos0[si['pelvis']][[0, 2]] - pos0[si['root']][[0, 2]])) * scale
    pelvis_world[:, 1] = pel[:, 1] * scale
    if vertical is not None and vertical[0] == 'inplace':
        # loop sem deslocamento: o pé apoiado escorrega para trás na velocidade da corrida
        speed = inplace_speed(src, clip, times, scale)
        root_pos[:, 2] = speed * (times - times[0])
        pelvis_world[:, 2] += root_pos[:, 2]
    elif vertical is not None:
        kind, value = vertical
        if kind == 'above':
            root_pos[:, 1] = np.maximum(0.0, pelvis_world[:, 1] - value)
        else:
            pelvis_world[:, 1] -= value

    local_rot, local_pos = {}, {}
    G['Motion'] = root_rot
    for name in tgt.order:
        p = tgt.parent[name]
        if p is None:
            local_rot[name] = root_rot
            local_pos[name] = root_pos
            continue
        local_rot[name] = np.swapaxes(G[p], -1, -2) @ G[name]
        if name == 'B_Pelvis':
            local_pos[name] = np.einsum('nji,nj->ni', root_rot, pelvis_world - root_pos)
        else:
            local_pos[name] = np.repeat(tgt.local_pos[name][None], n, 0)
    return times, local_rot, local_pos, scale


def inplace_speed(src, clip, times, scale):
    """Velocidade (m/s, escala do RPG) em que o pé de apoio desliza para trás num loop no lugar."""
    rot, pos = src.world(clip, times)
    toes = np.stack([pos[:, src.index['ball_l']], pos[:, src.index['ball_r']]], 1)    # [n, 2, 3]
    dt = np.diff(times)[:, None]
    vz = -(toes[1:, :, 2] - toes[:-1, :, 2]) / dt                                     # para trás = positivo
    h = toes[1:, :, 1]
    low = h < h.min() + 0.03                                                           # pé no chão
    v = vz[low]
    return float(np.median(v)) * scale


# ---------------------------------------------------------------- FBX

def euler_xyz(R):
    """Ângulos (graus) com R = Rz Ry Rx, contínuos ao longo dos frames."""
    cb = np.sqrt(R[:, 0, 0] ** 2 + R[:, 1, 0] ** 2)
    b = np.arctan2(-R[:, 2, 0], cb)
    a = np.arctan2(R[:, 2, 1], R[:, 2, 2])
    c = np.arctan2(R[:, 1, 0], R[:, 0, 0])
    lock = cb < 1e-5                       # gimbal lock: só a soma/diferença de a e c importa
    a[lock] = np.arctan2(-R[lock, 1, 2], R[lock, 1, 1])
    c[lock] = 0.0
    e = np.stack([a, b, c], -1)
    out = e.copy()
    for k in range(1, len(e)):
        prev = out[k - 1]
        cands = [e[k], np.array([e[k][0] + np.pi, np.pi - e[k][1], e[k][2] + np.pi])]
        best, bestd = None, 1e9
        for cnd in cands:
            cnd = cnd + 2 * np.pi * np.round((prev - cnd) / (2 * np.pi))
            d = np.abs(cnd - prev).sum()
            if d < bestd:
                best, bestd = cnd, d
        out[k] = best
    return np.degrees(out)


def write_fbx(path, times, local_rot, local_pos):
    root = fbx.read(TEMPLATE_FBX)
    sc = fbx.Scene(TEMPLATE_FBX)
    objs = {n.props[0]: n for n in root.find('Objects').children}
    n = len(times)
    ktimes = [int(round(t * fbx.KTIME)) for t in times]
    stop = ktimes[-1]

    # curvas de cada (nó, canal, eixo)
    node_target = {}
    for ch, pa, prop in sc.op:
        if objs.get(ch) is not None and objs[ch].name == 'AnimationCurveNode' and pa in sc.models:
            node_target[ch] = (sc.name[pa], {'Lcl Translation': 'T', 'Lcl Rotation': 'R', 'Lcl Scaling': 'S'}.get(prop))
    curves = {}
    for ch, pa, prop in sc.op:
        if objs.get(ch) is not None and objs[ch].name == 'AnimationCurve' and pa in node_target:
            bone, chan = node_target[pa]
            curves[(bone, chan, prop[-1])] = (objs[ch], objs[pa])

    values = {}
    for name in local_rot:
        p = sc.props[sc.by_name[name]]
        pre = fbx.euler(p.get('PreRotation', [0, 0, 0]))
        post = fbx.euler(p.get('PostRotation', [0, 0, 0]))
        assert int(p.get('RotationOrder', [0])[0]) == 0
        Rf = F @ local_rot[name] @ F                     # Unity -> FBX
        Re = pre.T @ Rf @ post                            # tira pré/pós-rotação
        e = euler_xyz(Re)
        t = (local_pos[name] @ F.T) / 0.01
        for k, ax in enumerate('XYZ'):
            values[(name, 'R', ax)] = e[:, k]
            values[(name, 'T', ax)] = t[:, k]
            values[(name, 'S', ax)] = np.ones(n)

    for key, (curve, cnode) in curves.items():
        v = values.get(key)
        if v is None:
            continue
        for c in curve.children:
            if c.name == 'Default':
                c.props = [float(v[0])]
            elif c.name == 'KeyTime':
                c.props = [ktimes]
            elif c.name == 'KeyValueFloat':
                c.props = [[float(x) for x in v]]
            elif c.name == 'KeyAttrFlags':
                c.props = [[4 | 0x100]]            # interpolação linear
            elif c.name == 'KeyAttrDataFloat':
                c.props = [[0.0, 0.0, 0.0, 0.0]]
            elif c.name == 'KeyAttrRefCount':
                c.props = [[n]]
        p70 = cnode.find('Properties70')
        for P in p70.findall('P'):
            if P.props[0].startswith('d|'):
                ax = P.props[0][-1]
                vv = values.get((key[0], key[1], ax))
                if vv is not None:
                    P.props[4] = float(vv[0])

    # duração
    for o in root.find('Objects').children:
        if o.name == 'AnimationStack':
            for P in o.find('Properties70').findall('P'):
                if P.props[0] in ('LocalStop', 'ReferenceStop'):
                    P.props[4] = stop
    for P in root.find('GlobalSettings').find('Properties70').findall('P'):
        if P.props[0] == 'TimeSpanStop':
            P.props[4] = stop
    take = root.find('Takes').find('Take')
    for c in take.children:
        if c.name in ('LocalTime', 'ReferenceTime'):
            c.props = [0, stop]
    fbx.write(path, root)


# ---------------------------------------------------------------- .meta

def guid_for(name):
    return hashlib.md5(('rollback-quaternius-' + name).encode()).hexdigest()


def write_meta(path, clip_name, frames, loop, vertical_root_motion=False):
    s = open(TEMPLATE_META).read()
    s = re.sub(r'guid: [0-9a-f]{32}', f'guid: {guid_for(clip_name)}', s, count=1)
    # tabela de ids antiga (nós do arquivo original) -> vazia
    s = re.sub(r'  internalIDToNameTable:\n(?:  [ -].*\n)*?  externalObjects:', '  internalIDToNameTable: []\n  externalObjects:', s)
    s = re.sub(r'    animationImportWarnings: .*?\n    animationRetargetingWarnings: .*?\n    animationDoRetargetingWarnings',
               '    animationImportWarnings: \n    animationRetargetingWarnings: \n    animationDoRetargetingWarnings', s, flags=re.S)
    clip = f"""    clipAnimations:
    - serializedVersion: 16
      name: {clip_name}
      takeName: Take 001
      internalID: 7400000
      firstFrame: 0
      lastFrame: {frames - 1}
      wrapMode: 0
      orientationOffsetY: 0
      level: 0
      cycleOffset: 0
      loop: 0
      hasAdditiveReferencePose: 0
      loopTime: {1 if loop else 0}
      loopBlend: {1 if loop else 0}
      loopBlendOrientation: 1
      loopBlendPositionY: {0 if vertical_root_motion else 1}
      loopBlendPositionXZ: 0
      keepOriginalOrientation: 1
      keepOriginalPositionY: 1
      keepOriginalPositionXZ: 1
      heightFromFeet: 0
      mirror: 0
      bodyMask: 01000000010000000100000001000000010000000100000001000000010000000100000001000000010000000100000001000000
      curves: []
      events: []
      transformMask: []
      maskType: 3
      maskSource: {{instanceID: 0}}
      additiveReferencePoseFrame: 0
    isReadable: 1
"""
    s = re.sub(r'    clipAnimations:\n.*?    isReadable: 1\n', clip, s, flags=re.S)
    s = re.sub(r'AssetOrigin:\n(?:  .*\n?)*', '', s)
    open(path, 'w').write(s.rstrip('\n') + '\n')


def main():
    download()
    os.makedirs(OUT, exist_ok=True)
    tgt = Target()
    sources = {}
    report = []
    for entry in CLIPS:
        fname, clip, name, loop, root_motion = entry[:5]
        vertical = entry[5] if len(entry) > 5 else None
        if fname not in sources:
            sources[fname] = Source(os.path.join(SOURCE, fname))
        src = sources[fname]
        times, lrot, lpos, scale = retarget(src, tgt, clip, root_motion, vertical)
        path = os.path.join(OUT, name + '.fbx')
        write_fbx(path, times, lrot, lpos)
        write_meta(path + '.meta', name, len(times), loop, vertical is not None and vertical[0] == 'above')
        travel = np.linalg.norm(lpos['Motion'][-1] - lpos['Motion'][0])
        speed = travel / max(times[-1], 1e-6)
        report.append(f"{name:28s} {len(times):4d} frames  deslocamento {travel:5.2f} m  ({speed:4.1f} m/s)")
    folder_meta = OUT + '.meta'
    if not os.path.exists(folder_meta):
        open(folder_meta, 'w').write(f"fileFormatVersion: 2\nguid: {guid_for('folder')}\nfolderAsset: yes\n"
                                     "DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    print('\n'.join(report))
    print(f"escala (quadril RPG / Quaternius): {scale:.3f}")


if __name__ == '__main__':
    main()
