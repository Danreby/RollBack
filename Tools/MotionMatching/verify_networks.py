"""Gera entradas/saídas de referência das redes treinadas para conferir a inferência em C#.

  python3 verify_networks.py && dotnet run --project Harness -c Release -- \
      ../../Assets/MotionMatching/PlayerLocomotion.bytes --verify build/verify.bin
"""
import os
import struct
import numpy as np
import quat

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, 'build')


def forward(m, x):
    n = int(m['nlayers'])
    h = (x - m['mean_in']) / m['std_in']
    for i in range(n):
        h = h @ m[f'w{i}'].astype(np.float64) + m[f'b{i}']
        if i < n - 1:
            h = np.maximum(h, 0.0)
    return h * m['std_out'] + m['mean_out']


def main(samples=64):
    d = np.load(os.path.join(BUILD, 'database.npz'))
    X = d['features'].astype(np.float64)
    Z = np.load(os.path.join(BUILD, 'latent.npy')).astype(np.float64)
    rng = np.random.default_rng(3)
    idx = rng.integers(0, len(X), samples)
    nb = len(d['bone_names'])
    out = []
    for name in ('decompressor', 'stepper', 'projector'):
        m = {k: v.astype(np.float64) if v.dtype.kind == 'f' else v for k, v in np.load(os.path.join(BUILD, f'lmm_{name}.npz')).items()}
        if name == 'projector':
            x = X[idx] + rng.normal(0, 0.3, X[idx].shape)
        else:
            x = np.concatenate([X[idx], Z[idx]], -1)
        y = forward(m, x)
        out.append(struct.pack('<iii', samples, x.shape[1], y.shape[1]))
        for k in range(samples):
            out.append(x[k].astype('<f4').tobytes())
            out.append(y[k].astype('<f4').tobytes())
            if name == 'decompressor':
                txy = y[k, 3:3 + (nb - 1) * 6].reshape(nb - 1, 3, 2)
                out.append(quat.from_xform_xy(txy).astype('<f4').tobytes())
    with open(os.path.join(BUILD, 'verify.bin'), 'wb') as f:
        f.write(b''.join(out))
    print('build/verify.bin gravado')


if __name__ == '__main__':
    main()
