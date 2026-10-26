"""Avalia as redes treinadas contra o banco: reconstrução do Decompressor, deriva do Stepper em
rollouts longos e acerto do Projector (frame mais parecido). python3 evaluate_lmm.py"""
import os
import numpy as np
import quat
from verify_networks import forward

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, 'build')


def load(name):
    return {k: (v.astype(np.float64) if v.dtype.kind == 'f' else v)
            for k, v in np.load(os.path.join(BUILD, f'lmm_{name}.npz')).items()}


def joints(d, Y):
    nb = len(d['bone_names'])
    rot = quat.from_xform_xy(Y[:, 3:3 + (nb - 1) * 6].reshape(-1, nb - 1, 3, 2))
    lr = np.concatenate([np.tile([[[0, 0, 0, 1.0]]], (len(Y), 1, 1)), rot], 1)
    lp = np.repeat(d['bind_local_positions'][None].astype(np.float64), len(Y), 0)
    lp[:, 0] = 0
    lp[:, 1] = Y[:, :3]
    return quat.fk(lr, lp, d['bone_parents'])[1]


def ground_truth_joints(d, idx):
    lr = d['bone_rotations'][idx].astype(np.float64).copy()
    lr[:, 0] = [0, 0, 0, 1]
    lp = np.repeat(d['bind_local_positions'][None].astype(np.float64), len(idx), 0)
    lp[:, 0] = 0
    lp[:, 1] = d['bone_positions'][idx, 1]
    return quat.fk(lr, lp, d['bone_parents'])[1]


def main():
    d = np.load(os.path.join(BUILD, 'database.npz'))
    X = d['features'].astype(np.float64)
    Z = np.load(os.path.join(BUILD, 'latent.npy')).astype(np.float64)
    nf, nz = X.shape[1], Z.shape[1]
    dec, step, proj = load('decompressor'), load('stepper'), load('projector')

    Y = forward(dec, np.concatenate([X, Z], -1))
    e = np.linalg.norm(joints(d, Y) - ground_truth_joints(d, np.arange(len(X))), axis=-1) * 100
    print(f"Decompressor: erro das juntas médio {e.mean():.2f} cm, p99 {np.percentile(e, 99):.2f} cm, máx {e.max():.2f} cm")

    # Stepper: rollouts de 2 s a partir de frames aleatórios, comparados ao banco
    rng = np.random.default_rng(0)
    starts = rng.integers(0, len(X), 64)
    x, z = X[starts].copy(), Z[starts].copy()
    idx = starts.copy()
    errs = []
    for k in range(120):
        out = forward(step, np.concatenate([x, z], -1))
        x = x + out[:, :nf] / 60.0
        z = z + out[:, nf:] / 60.0
        idx = d['next_frame'][idx]
        if k in (19, 59, 119):
            Yr = forward(dec, np.concatenate([x, z], -1))
            ej = np.linalg.norm(joints(d, Yr) - ground_truth_joints(d, idx), axis=-1).mean() * 100
            ex = np.abs(x - X[idx]).mean()
            errs.append((k + 1, ej, ex))
    for frames, ej, ex in errs:
        print(f"Stepper: depois de {frames:3d} frames sem projeção: erro das juntas {ej:.2f} cm, erro das features {ex:.3f}")

    # Projector: consultas com ruído -> frame mais parecido
    q = X[rng.integers(0, len(X), 2000)]
    q = q + rng.normal(0, 0.2, q.shape) * X.std(0)
    out = forward(proj, q)
    dist = ((q[:, None, :] - X[None]) ** 2).sum(-1)
    nn = dist.argmin(1)
    best = dist.min(1)
    got = ((q - out[:, :nf]) ** 2).sum(-1)
    print(f"Projector: custo médio do vizinho real {best.mean():.3f}, da projeção {got.mean():.3f}; "
          f"erro das features {np.abs(out[:, :nf] - X[nn]).mean():.3f}")


if __name__ == '__main__':
    main()
