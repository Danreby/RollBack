"""Mede e desenha as poses gravadas pelo Harness (núcleo C# do Motion Matching rodando fora da Unity).

  python3 analyze.py build/harness/<cenário>_<modo>.bin [--sheet saida.png] [--gif saida.gif]

Métricas: deslize dos pés (velocidade horizontal da ponta do pé enquanto ela está no chão),
picos de velocidade angular (estalos), distância da raiz visual até a cápsula.
"""
import os
import sys
import argparse
import numpy as np
import quat

HERE = os.path.dirname(os.path.abspath(__file__))


def load_run(path, nb):
    raw = np.fromfile(path, dtype='<f4')
    n, width = int(raw[:1].view('<i4')[0]), int(raw[1:2].view('<i4')[0])
    rows = raw[2:2 + n * width].reshape(n, width)
    c = 0
    def take(k):
        nonlocal c
        out = rows[:, c:c + k]
        c += k
        return out
    run = dict(t=take(1)[:, 0], dt=take(1)[:, 0], sim_pos=take(3), sim_rot=take(4), root_offset=take(3),
               source=take(1)[:, 0], frame=take(1)[:, 0], lock=take(2), contact=take(2), stepping=take(2), cost=take(1)[:, 0], speed=take(1)[:, 0],
               stance=take(1)[:, 0], hips=take(3), rot=take(nb * 4).reshape(n, nb, 4),
               anim_hips=take(3), anim_rot=take(nb * 4).reshape(n, nb, 4))
    return run


def world_pose(run, db, animated=False):
    parents = db['bone_parents']
    bind = db['bind_local_positions']
    n, nb = run['rot'].shape[:2]
    lrot = run['anim_rot' if animated else 'rot'].astype(np.float64).copy()
    lrot[:, 0] = run['sim_rot']
    lpos = np.repeat(bind[None].astype(np.float64), n, axis=0)
    lpos[:, 0] = run['sim_pos']
    lpos[:, 1] = run['anim_hips' if animated else 'hips']
    return quat.fk(quat.normalize(lrot), lpos, parents)


def metrics(run, db):
    names = [str(x) for x in db['bone_names']]
    grot, gpos = world_pose(run, db)
    dt = run['dt'][1:, None]
    out = {}
    slide = []
    for f, toe in enumerate(('B_L_Toe0', 'B_R_Toe0')):
        i = names.index(toe)
        p = gpos[:, i]
        v = np.linalg.norm((p[1:, [0, 2]] - p[:-1, [0, 2]]) / dt, axis=-1)
        # pé que a animação diz estar no chão (contato) e que está de fato perto do chão
        ground = (run['contact'][1:, f] > 0.5) & (run['contact'][:-1, f] > 0.5) & (p[1:, 1] < 0.08)
        slide.append(v[ground])
    toes = [names.index(t) for t in ('B_L_Toe0', 'B_R_Toe0')]
    jump = np.linalg.norm(np.diff(gpos[:, toes], axis=0), axis=-1).max(axis=1)
    out['salto_pe_max_cm_frame'] = float(jump.max() * 100)
    s = np.concatenate(slide)
    out['deslize_pes_medio_cm_s'] = float(s.mean() * 100) if len(s) else 0.0
    out['deslize_pes_p95_cm_s'] = float(np.percentile(s, 95) * 100) if len(s) else 0.0
    # velocidade angular global dos ossos (exclui dedos)
    keep = [k for k, n in enumerate(names) if 'Finger' not in n and k > 0]
    d = quat.to_scaled_angle_axis(quat.abs(quat.mul_inv(grot[1:, keep], grot[:-1, keep])))
    w = np.degrees(np.linalg.norm(d, axis=-1)) / dt
    out['vel_angular_p99_graus_s'] = float(np.percentile(w, 99))
    out['vel_angular_max_graus_s'] = float(w.max())
    worst = np.unravel_index(np.argmax(w), w.shape)
    out['pico_em'] = f"t={run['t'][worst[0] + 1]:.2f}s osso={names[keep[worst[1]]]}"
    # aceleração angular (troca brusca de velocidade = estalo)
    acc = np.linalg.norm(np.diff(d / dt[..., None], axis=0), axis=-1) / dt[1:]
    out['acel_angular_p99'] = float(np.degrees(np.percentile(acc, 99)))
    out['raiz_offset_max_cm'] = float(np.linalg.norm(run['root_offset'], axis=-1).max() * 100)
    hips_v = np.linalg.norm(np.diff(gpos[:, 1], axis=0) / dt, axis=-1)
    out['quadril_vel_max'] = float(hips_v.max())
    return out, grot, gpos


def stick_sheet(gpos, db, run, path, every=6, cols=10, max_frames=60):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    parents = db['bone_parents']
    names = [str(x) for x in db['bone_names']]
    idx = list(range(0, len(gpos), every))[:max_frames]
    rows = (len(idx) + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(cols * 1.6, rows * 2.6))
    for ax in np.atleast_1d(axes).ravel():
        ax.axis('off')
    for ax, f in zip(np.atleast_1d(axes).ravel(), idx):
        P = gpos[f]
        fwd = quat.mul_vec(run['sim_rot'][f].astype(np.float64), np.array([0.0, 0, 1]))
        right = np.cross([0, 1, 0], fwd)
        for i in range(1, len(parents)):
            if parents[i] <= 0 or 'Finger' in names[i]:
                continue
            a, b = P[parents[i]], P[i]
            col = 'tab:blue' if '_L_' in names[i] else ('tab:red' if '_R_' in names[i] else 'k')
            # vista lateral (eixo horizontal = frente da simulação)
            ax.plot([np.dot(a - P[0], fwd), np.dot(b - P[0], fwd)], [a[1], b[1]], color=col, lw=1.2)
        ax.axhline(0, color='0.7', lw=0.5)
        ax.set_xlim(-1.3, 1.3); ax.set_ylim(-0.05, 2.5); ax.set_aspect('equal')
        ax.set_title(f"{run['t'][f]:.2f}s v={run['speed'][f]:.1f}", fontsize=7)
    plt.tight_layout()
    plt.savefig(path, dpi=70)
    plt.close(fig)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('runs', nargs='+')
    ap.add_argument('--sheet', default=None)
    args = ap.parse_args()
    db = np.load(os.path.join(HERE, 'build', 'database.npz'))
    nb = len(db['bone_names'])
    for path in args.runs:
        run = load_run(path, nb)
        m, grot, gpos = metrics(run, db)
        print(os.path.basename(path), ' '.join(f"{k}={v:.1f}" if isinstance(v, float) else f"{k}=[{v}]" for k, v in m.items()))
        if args.sheet:
            stick_sheet(gpos, db, run, args.sheet if len(args.runs) == 1 else path.replace('.bin', '.png'))


if __name__ == '__main__':
    main()
