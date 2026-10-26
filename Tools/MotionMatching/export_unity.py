"""Grava o banco de movimento e as redes do LMM no arquivo lido pela Unity (TextAsset .bytes).

Formato (little endian), lido por Assets/Scripts/MotionMatching/Core/MotionDatabase.cs:
  "LMMD", versão
  esqueleto: nº de ossos, nomes, pais, posições locais do bind
  papéis (quadril, pernas, pés, dedos) -> índice do osso
  frames: rotações locais, posição/velocidade do quadril, velocidade da raiz, contatos, próximo frame
  trechos: início, fim, postura, tipo, velocidade, escala
  features normalizadas + offset/escala + grupos, horizonte da trajetória
  redes: Decompressor, Stepper, Projector (camadas, normalização, pesos)
"""
import os
import struct
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, 'build')
OUT = os.path.join(HERE, '..', '..', 'Assets', 'MotionMatching', 'PlayerLocomotion.bytes')
META_GUID = '4f6b1d2a9c3e4b7d8a1f0e2d3c4b5a69'

ROLES = {
    'Hips': 'B_Pelvis',
    'LeftUpperLeg': 'B_L_Thigh', 'LeftLowerLeg': 'B_L_Calf', 'LeftFoot': 'B_L_Foot', 'LeftToes': 'B_L_Toe0',
    'RightUpperLeg': 'B_R_Thigh', 'RightLowerLeg': 'B_R_Calf', 'RightFoot': 'B_R_Foot', 'RightToes': 'B_R_Toe0',
}
TRAJ_FRAMES = [20, 40, 60]
ACTIVATION = {'decompressor': 0, 'stepper': 0, 'projector': 0}  # 0 = ReLU, 1 = ELU


class Writer:
    def __init__(self):
        self.parts = []

    def i32(self, v):
        self.parts.append(struct.pack('<i', int(v)))

    def f32(self, v):
        self.parts.append(struct.pack('<f', float(v)))

    def s(self, text):
        b = str(text).encode('utf-8')
        self.i32(len(b))
        self.parts.append(b)

    def ints(self, a):
        self.parts.append(np.ascontiguousarray(a, dtype='<i4').tobytes())

    def floats(self, a):
        self.parts.append(np.ascontiguousarray(a, dtype='<f4').tobytes())

    def raw(self, b):
        self.parts.append(bytes(b))

    def data(self):
        return b''.join(self.parts)


def write_mlp(w, name):
    path = os.path.join(BUILD, f'lmm_{name}.npz')
    m = np.load(path)
    n = int(m['nlayers'])
    sizes = [m['w0'].shape[0]] + [m[f'w{i}'].shape[1] for i in range(n)]
    w.s(name)
    w.i32(ACTIVATION[name])
    w.i32(n)
    w.ints(sizes)
    w.floats(m['mean_in']); w.floats(m['std_in'])
    w.floats(m['mean_out']); w.floats(m['std_out'])
    for i in range(n):
        w.floats(m[f'w{i}'])          # [entrada, saída], linha a linha
        w.floats(m[f'b{i}'])
    return sizes


def export(with_networks=True):
    d = np.load(os.path.join(BUILD, 'database.npz'))
    names = [str(n) for n in d['bone_names']]
    nb = len(names)
    F = d['bone_rotations'].shape[0]
    w = Writer()
    w.raw(b'LMMD')
    w.i32(1)

    w.i32(nb)
    for n in names:
        w.s(n)
    w.ints(d['bone_parents'])
    w.floats(d['bind_local_positions'])

    w.i32(len(ROLES))
    for role, bone in ROLES.items():
        w.s(role)
        w.i32(names.index(bone))

    w.i32(F)
    rot = d['bone_rotations'].astype(np.float32)
    w.floats(rot)
    w.floats(d['bone_angular_velocities'])
    w.floats(d['bone_positions'][:, 1])
    w.floats(d['bone_velocities'][:, 1])
    w.floats(d['bone_velocities'][:, 0])          # a raiz do banco está sempre virada para +Z
    w.floats(d['bone_angular_velocities'][:, 0])
    w.raw(d['contact_states'].astype(np.uint8).tobytes())
    w.ints(d['next_frame'])

    R = len(d['range_starts'])
    w.i32(R)
    w.ints(d['range_starts']); w.ints(d['range_stops'])
    w.ints(d['range_stance']); w.ints(d['range_kind'])
    w.floats(d['range_speed']); w.floats(d['range_scale'])

    nf = d['features'].shape[1]
    w.i32(nf)
    w.floats(d['features'])
    w.floats(d['features_offset']); w.floats(d['features_scale'])
    groups = list(zip(d['feature_groups'], d['feature_sizes'], d['feature_weights']))
    w.i32(len(groups))
    for name, size, weight in groups:
        w.s(name); w.i32(size); w.f32(weight)
    w.i32(len(TRAJ_FRAMES))
    w.ints(TRAJ_FRAMES)

    if with_networks:
        w.i32(1)
        latent = np.load(os.path.join(BUILD, 'latent.npy'))
        w.i32(latent.shape[1])
        info = {k: write_mlp(w, k) for k in ('decompressor', 'stepper', 'projector')}
    else:
        w.i32(0)
        info = {}

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    data = w.data()
    with open(OUT, 'wb') as f:
        f.write(data)
    meta = OUT + '.meta'
    if not os.path.exists(meta):
        with open(meta, 'w') as f:
            f.write(f"fileFormatVersion: 2\nguid: {META_GUID}\nTextScriptImporter:\n  externalObjects: {{}}\n"
                    f"  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    return OUT, len(data), info


if __name__ == '__main__':
    import sys
    path, size, info = export(with_networks='--no-networks' not in sys.argv)
    print(f"{os.path.normpath(path)}: {size / 1e6:.2f} MB")
    for k, v in info.items():
        print(f"  {k}: {v}")
