"""Treina as redes do Learned Motion Matching (Holden, Kanoun, Perepichka e Popa, SIGGRAPH 2020).

  * Compressor  (só no treino): pose completa -> variáveis latentes Z (o que as features não dizem)
  * Decompressor: features X + latentes Z -> pose (rotações locais, quadril, velocidade da raiz, contatos)
  * Stepper:      (X, Z) do frame atual -> velocidades de X e Z (avança a animação sem o banco)
  * Projector:    consulta (pose atual + trajetória desejada) -> (X, Z) do frame mais parecido do banco

Com as três redes o jogo não precisa do banco de poses em tempo de execução: o Projector faz o
papel da busca, o Stepper o da reprodução frame a frame e o Decompressor gera a pose.

Uso:  python3 train_lmm.py [decompressor|stepper|projector|all] [--iters N]
Entrada: build/database.npz (build_database.py). Saída: build/lmm_*.npz
"""
import os
import sys
import time
import argparse
import numpy as np
import jax
import jax.numpy as jnp
import optax
import quat

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, 'build')
DT = 1.0 / 60.0
NLATENT = 32
HIDDEN = {'compressor': 512, 'decompressor': 384, 'stepper': 384, 'projector': 384}


# ---------------------------------------------------------------- redes

def init_mlp(key, sizes):
    """Inicialização igual à nn.Linear do PyTorch: U(-1/sqrt(fan_in), 1/sqrt(fan_in))."""
    params = []
    for fan_in, fan_out in zip(sizes[:-1], sizes[1:]):
        key, kw, kb = jax.random.split(key, 3)
        bound = 1.0 / np.sqrt(fan_in)
        params.append((jax.random.uniform(kw, (fan_in, fan_out), minval=-bound, maxval=bound),
                       jax.random.uniform(kb, (fan_out,), minval=-bound, maxval=bound)))
    return params


def mlp(params, x, act):
    for i, (w, b) in enumerate(params):
        x = x @ w + b
        if i < len(params) - 1:
            x = act(x)
    return x


def make_optimizer(lr, iters, decay_every=1000, gamma=0.99):
    # AdamW com AMSGrad e decaimento exponencial (como no código original)
    schedule = optax.exponential_decay(lr, decay_every, gamma, staircase=True)
    return optax.chain(optax.scale_by_amsgrad(), optax.add_decayed_weights(0.001),
                       optax.scale_by_learning_rate(schedule))


# ---------------------------------------------------------------- dados

def load():
    d = dict(np.load(os.path.join(BUILD, 'database.npz')))
    return d


def rot6_to_matrix(x):
    """[..., 3, 2] (duas colunas) -> matriz ortonormal [..., 3, 3] (Gram-Schmidt)."""
    c0 = x[..., 0] / jnp.sqrt(jnp.sum(x[..., 0] ** 2, axis=-1, keepdims=True) + 1e-8)
    c2 = jnp.cross(c0, x[..., 1])
    c2 = c2 / jnp.sqrt(jnp.sum(c2 ** 2, axis=-1, keepdims=True) + 1e-8)
    c1 = jnp.cross(c2, c0)
    return jnp.stack([c0, c1, c2], axis=-1)


def fk_matrix(lrot, lpos, parents):
    grot, gpos = [lrot[..., 0, :, :]], [lpos[..., 0, :]]
    for i in range(1, len(parents)):
        p = parents[i]
        gpos.append(jnp.einsum('...ij,...j->...i', grot[p], lpos[..., i, :]) + gpos[p])
        grot.append(grot[p] @ lrot[..., i, :, :])
    return jnp.stack(grot, axis=-3), jnp.stack(gpos, axis=-2)


class Data:
    """Arrays do banco no formato usado pelas redes (todos float32)."""

    def __init__(self, d):
        self.d = d
        self.parents = d['bone_parents']
        self.nb = len(self.parents)
        self.X = d['features'].astype(np.float32)
        self.next = d['next_frame']
        self.contacts = d['contact_states'].astype(np.float32)
        Yrot, Ypos = d['bone_rotations'], d['bone_positions']
        Yvel, Yang = d['bone_velocities'], d['bone_angular_velocities']
        Grot, Gpos, Gvel, Gang = quat.fk_vel(Yrot, Ypos, Yvel, Yang, self.parents)
        Qrot = quat.inv_mul(Grot[:, 0:1], Grot)
        Qpos = quat.inv_mul_vec(Grot[:, 0:1], Gpos - Gpos[:, 0:1])
        Qvel = quat.inv_mul_vec(Grot[:, 0:1], Gvel)
        Qang = quat.inv_mul_vec(Grot[:, 0:1], Gang)
        f = np.float32
        self.Ypos, self.Ytxy = Ypos.astype(f), quat.to_xform_xy(Yrot).astype(f)
        self.Yvel, self.Yang = Yvel.astype(f), Yang.astype(f)
        self.Qpos, self.Qtxy = Qpos.astype(f), quat.to_xform_xy(Qrot).astype(f)
        self.Qvel, self.Qang = Qvel.astype(f), Qang.astype(f)
        self.Yrvel = quat.inv_mul_vec(Yrot[:, 0], Yvel[:, 0]).astype(f)
        self.Yrang = quat.inv_mul_vec(Yrot[:, 0], Yang[:, 0]).astype(f)
        self.bind = d['bind_local_positions'].astype(f)
        n = len(self.X)
        # Entrada do compressor: pose completa (local e no espaço do personagem)
        self.Cin = np.concatenate([
            self.Ypos[:, 1], self.Ytxy[:, 1:].reshape(n, -1), self.Yvel[:, 1:].reshape(n, -1),
            self.Yang[:, 1:].reshape(n, -1), self.Qpos[:, 1:].reshape(n, -1), self.Qtxy[:, 1:].reshape(n, -1),
            self.Qvel[:, 1:].reshape(n, -1), self.Qang[:, 1:].reshape(n, -1), self.Yrvel, self.Yrang,
            self.contacts], axis=-1)
        nb1 = self.nb - 1
        groups = [3, nb1 * 6, nb1 * 3, nb1 * 3, nb1 * 3, nb1 * 6, nb1 * 3, nb1 * 3, 3, 3, 2]
        self.Cmean = self.Cin.mean(0)
        self.Cstd = group_std(self.Cin, groups)
        # Saída do decompressor: quadril, rotações (6 números), velocidade da raiz, contatos
        self.Dout = self.decompressor_target(np.arange(n))
        self.Dmean = self.Dout.mean(0)
        self.Dstd = group_std(self.Dout, [3, nb1 * 6, 3, 3, 2])

    def decompressor_target(self, idx):
        k = len(idx)
        return np.concatenate([self.Ypos[idx, 1], self.Ytxy[idx, 1:].reshape(k, -1),
                               self.Yrvel[idx], self.Yrang[idx], self.contacts[idx]], axis=-1)


def group_std(A, sizes):
    out, c = [], 0
    for s in sizes:
        std = max(float(A[:, c:c + s].std()), 1e-4)
        out.append(np.full(s, std, np.float32))
        c += s
    return np.concatenate(out)


def split_decompressed(Y, nb):
    nb1 = nb - 1
    hips = Y[..., 0:3]
    txy = Y[..., 3:3 + nb1 * 6].reshape(Y.shape[:-1] + (nb1, 3, 2))
    o = 3 + nb1 * 6
    return hips, txy, Y[..., o:o + 3], Y[..., o + 3:o + 6], Y[..., o + 6:o + 8]


# ---------------------------------------------------------------- 1) compressor + decompressor

def train_decompressor(data, iters, seed=1234, batch=64, lr=1e-3):
    nb = data.nb
    parents = data.parents
    nfeat = data.X.shape[1]
    key = jax.random.PRNGKey(seed)
    kc, kd = jax.random.split(key)
    params = {
        'compressor': init_mlp(kc, [data.Cin.shape[1]] + [HIDDEN['compressor']] * 3 + [NLATENT]),
        'decompressor': init_mlp(kd, [nfeat + NLATENT, HIDDEN['decompressor'], data.Dout.shape[1]]),
    }
    opt = make_optimizer(lr, iters)
    opt_state = opt.init(params)

    J = {k: jnp.asarray(v) for k, v in dict(
        X=data.X, Cin=data.Cin, Ypos=data.Ypos, Ytxy=data.Ytxy, Qpos=data.Qpos, Qtxy=data.Qtxy,
        Yrvel=data.Yrvel, Yrang=data.Yrang, contacts=data.contacts, Cmean=data.Cmean, Cstd=data.Cstd,
        Dmean=data.Dmean, Dstd=data.Dstd, bind=data.bind).items()}

    def decode(params, idx):
        Z = mlp(params['compressor'], (J['Cin'][idx] - J['Cmean']) / J['Cstd'], jax.nn.elu)
        Y = mlp(params['decompressor'], jnp.concatenate([J['X'][idx], Z], -1), jax.nn.relu) * J['Dstd'] + J['Dmean']
        return Z, Y

    def loss_fn(params, idx):
        Z, Y = decode(params, idx)                                   # idx: [B, 2]
        hips, txy, rvel, rang, contacts = split_decompressed(Y, nb)
        Ytil_txy = jnp.concatenate([J['Ytxy'][idx][:, :, 0:1], txy], axis=2)
        Ytil_pos = jnp.concatenate([J['Ypos'][idx][:, :, 0:1], hips[:, :, None],
                                    jnp.broadcast_to(J['bind'][2:], idx.shape + J['bind'][2:].shape)], axis=2)
        Grot, Gpos = fk_matrix(rot6_to_matrix(Ytil_txy), Ytil_pos, parents)
        R0 = jnp.swapaxes(Grot[:, :, 0:1], -1, -2)
        Qtil_rot = R0 @ Grot
        Qtil_pos = jnp.einsum('...ij,...j->...i', R0, Gpos - Gpos[:, :, 0:1])
        Qtil_txy = Qtil_rot[..., :2]

        gnd_hips, gnd_txy = J['Ypos'][idx][:, :, 1], J['Ytxy'][idx]
        gnd_Qpos, gnd_Qtxy = J['Qpos'][idx], J['Qtxy'][idx]

        def d(a):
            return (a[:, 1] - a[:, 0]) / DT

        loss = (jnp.mean(75.0 * jnp.abs(gnd_hips - hips))
                + jnp.mean(10.0 * jnp.abs(gnd_txy - Ytil_txy))
                + jnp.mean(2.0 * jnp.abs(J['Yrvel'][idx] - rvel))
                + jnp.mean(2.0 * jnp.abs(J['Yrang'][idx] - rang))
                + jnp.mean(2.0 * jnp.abs(J['contacts'][idx] - contacts))
                + jnp.mean(15.0 * jnp.abs(gnd_Qpos - Qtil_pos))
                + jnp.mean(5.0 * jnp.abs(gnd_Qtxy - Qtil_txy))
                + jnp.mean(10.0 * jnp.abs(d(gnd_hips) - d(hips)))
                + jnp.mean(1.75 * jnp.abs(d(gnd_txy) - d(Ytil_txy)))
                + jnp.mean(2.0 * jnp.abs(d(gnd_Qpos) - d(Qtil_pos)))
                + jnp.mean(0.75 * jnp.abs(d(gnd_Qtxy) - d(Qtil_txy)))
                + jnp.mean(0.1 * jnp.abs(Z)) + jnp.mean(0.1 * jnp.square(Z))
                + jnp.mean(0.01 * jnp.abs(d(Z))))
        return loss

    @jax.jit
    def step(params, opt_state, idx):
        loss, grads = jax.value_and_grad(loss_fn)(params, idx)
        updates, opt_state = opt.update(grads, opt_state, params)
        return optax.apply_updates(params, updates), opt_state, loss

    rng = np.random.default_rng(seed)
    n = len(data.X)
    params = run_loop('decompressor', step, params, opt_state, iters,
                      lambda: (lambda i: np.stack([i, data.next[i]], 1))(rng.integers(0, n, batch)))

    Z = np.asarray(mlp(params['compressor'], (J['Cin'] - J['Cmean']) / J['Cstd'], jax.nn.elu))
    return params, Z


def run_loop(name, step, params, opt_state, iters, make_batch, report=1000):
    t0, rolling = time.time(), None
    for i in range(iters):
        params, opt_state, loss = step(params, opt_state, make_batch())
        if i % 100 == 0:
            l = float(loss)
            rolling = l if rolling is None else rolling * 0.95 + l * 0.05
        if i % report == 0 or i == iters - 1:
            print(f"[{name}] {i:7d} loss {rolling:.4f}  ({time.time() - t0:.0f}s)", flush=True)
    return params


# ---------------------------------------------------------------- 2) stepper

def train_stepper(data, Z, iters, seed=1234, batch=64, window=20, lr=1e-3):
    X = data.X
    nfeat = X.shape[1]
    nxt = data.next
    dX = (X[nxt] - X) / DT
    dZ = (Z[nxt] - Z) / DT
    mean_in = np.concatenate([X.mean(0), Z.mean(0)]).astype(np.float32)
    std_in = np.concatenate([np.full(nfeat, X.std()), np.full(NLATENT, Z.std())]).astype(np.float32)
    mean_out = np.concatenate([dX.mean(0), dZ.mean(0)]).astype(np.float32)
    std_out = np.concatenate([np.full(nfeat, dX.std()), np.full(NLATENT, dZ.std())]).astype(np.float32)

    params = init_mlp(jax.random.PRNGKey(seed), [nfeat + NLATENT, HIDDEN['stepper'], HIDDEN['stepper'], nfeat + NLATENT])
    opt = make_optimizer(lr, iters)
    opt_state = opt.init(params)
    JX, JZ = jnp.asarray(X), jnp.asarray(Z.astype(np.float32))
    mi, si, mo, so = map(jnp.asarray, (mean_in, std_in, mean_out, std_out))

    def loss_fn(params, idx):                                      # idx: [B, window]
        Xg, Zg = JX[idx], JZ[idx]
        xs, zs = [Xg[:, 0]], [Zg[:, 0]]
        for _ in range(1, window):
            out = mlp(params, (jnp.concatenate([xs[-1], zs[-1]], -1) - mi) / si, jax.nn.relu) * so + mo
            xs.append(xs[-1] + DT * out[:, :nfeat])
            zs.append(zs[-1] + DT * out[:, nfeat:])
        Xt, Zt = jnp.stack(xs, 1), jnp.stack(zs, 1)
        dv = lambda a: (a[:, 1:] - a[:, :-1]) / DT
        return (jnp.mean(2.0 * jnp.abs(Xg - Xt)) + jnp.mean(7.5 * jnp.abs(Zg - Zt))
                + jnp.mean(0.2 * jnp.abs(dv(Xg) - dv(Xt))) + jnp.mean(0.5 * jnp.abs(dv(Zg) - dv(Zt))))

    @jax.jit
    def step(params, opt_state, idx):
        loss, grads = jax.value_and_grad(loss_fn)(params, idx)
        updates, opt_state = opt.update(grads, opt_state, params)
        return optax.apply_updates(params, updates), opt_state, loss

    # janelas seguindo o próximo frame (os ciclos dão a volta)
    chains = np.zeros((len(X), window), int)
    chains[:, 0] = np.arange(len(X))
    for k in range(1, window):
        chains[:, k] = nxt[chains[:, k - 1]]
    rng = np.random.default_rng(seed)
    params = run_loop('stepper', step, params, opt_state, iters, lambda: chains[rng.integers(0, len(X), batch)])
    return params, dict(mean_in=mean_in, std_in=std_in, mean_out=mean_out, std_out=std_out)


# ---------------------------------------------------------------- 3) projector

def spring_damping(halflife):
    return (4.0 * 0.69314718056) / (halflife + 1e-5)


def predict_trajectory(v0, a0, vd, yaw0, w0, yawd, hv, hr, times):
    """Mesmas molas da simulação do jogo (críticamente amortecidas), em forma fechada."""
    y = spring_damping(hv) / 2.0
    j0 = v0 - vd
    j1 = a0 + j0 * y
    out_p, out_d = [], []
    yr = spring_damping(hr) / 2.0
    for t in times:
        e = np.exp(-y * t)
        p = e * ((-j1) / (y * y) + (-j0 - j1 * t) / y) + j1 / (y * y) + j0 / y + vd * t
        ang0 = yaw0 - yawd
        ang1 = w0 + ang0 * yr
        er = np.exp(-yr * t)
        yaw = er * (ang0 + ang1 * t) + yawd
        out_p.append(p)
        out_d.append(np.stack([np.sin(yaw), np.cos(yaw)], -1))
    return np.stack(out_p, 1), np.stack(out_d, 1)


def random_trajectories(n, rng):
    """Trajetórias (espaço do personagem, virado para +Z) parecidas com as do jogo."""
    speeds = np.array([0.0, 2.4, 4.8, 7.0, 12.5])
    def pick_speed(k):
        base = speeds[rng.integers(0, len(speeds), k)]
        return np.where(base > 0, base * rng.uniform(0.6, 1.15, k), 0.0) * (rng.random(k) > 0.05)
    # mira travada: o personagem encara o alvo e anda para qualquer lado (strafe)
    strafing = rng.random(n) < 0.35
    cur_speed = pick_speed(n)
    cur_ang = np.where((rng.random(n) < 0.7) & ~strafing, rng.normal(0, 0.15, n), rng.uniform(-np.pi, np.pi, n))
    v0 = np.stack([np.sin(cur_ang) * cur_speed, np.zeros(n), np.cos(cur_ang) * cur_speed], -1)
    a0 = rng.normal(0, 3.0, (n, 3)) * (rng.random((n, 1)) < 0.5); a0[:, 1] = 0
    des_speed = pick_speed(n)
    des_ang = np.where(rng.random(n) < 0.5, rng.normal(0, 0.2, n), rng.uniform(-np.pi, np.pi, n))
    vd = np.stack([np.sin(des_ang) * des_speed, np.zeros(n), np.cos(des_ang) * des_speed], -1)
    yawd = np.where((des_speed > 0) & ~strafing, des_ang, rng.normal(0, 0.3, n))
    des_ang = np.where(strafing, rng.uniform(-np.pi, np.pi, n), des_ang)
    vd = np.stack([np.sin(des_ang) * des_speed, np.zeros(n), np.cos(des_ang) * des_speed], -1)
    w0 = rng.normal(0, 2.0, n) * (rng.random(n) < 0.5)
    hv = rng.uniform(0.12, 0.3, n)[:, None]
    hr = rng.uniform(0.08, 0.25, n)
    p, dirs = predict_trajectory(v0, a0, vd, 0.0, w0, yawd, hv, hr, (20 * DT, 40 * DT, 60 * DT))
    traj = np.concatenate([p[:, :, [0, 2]].reshape(n, -1), dirs[:, :, [0, 1]].reshape(n, -1)], -1)
    # velocidade atual (x, z) e parado/em movimento, como a consulta do jogo monta
    moving = ((des_speed > 0.1) | (cur_speed > 0.8)).astype(np.float64)[:, None]
    return np.concatenate([traj, v0[:, [0, 2]], moving], -1)


def train_projector(data, Z, iters, seed=1234, batch=64, lr=1e-3, pool=400000):
    d = data.d
    X = data.X
    nfeat = X.shape[1]
    off, scale = d['features_offset'], d['features_scale']
    sizes = list(d['feature_sizes'])
    groups = list(d['feature_groups'])
    start = {g: sum(sizes[:k]) for k, g in enumerate(groups)}
    traj0 = start['trajectory_positions']      # início das features de trajetória
    traj1 = traj0 + sizes[groups.index('trajectory_positions')] + sizes[groups.index('trajectory_directions')]
    stance_col = start['stance']
    rv, mv = start['root_velocity'], start['moving']
    game_cols = list(range(traj0, traj1)) + [rv, rv + 1, mv]   # o que vem da simulação na consulta

    rng = np.random.default_rng(seed)
    traj = (random_trajectories(pool, rng) - off[game_cols]) / scale[game_cols]
    pose_std = X[:, :traj0].std(0)

    mean_in = X.mean(0).astype(np.float32)
    std_in = np.full(nfeat, X.std(), np.float32)
    mean_out = np.concatenate([X.mean(0), Z.mean(0)]).astype(np.float32)
    std_out = np.concatenate([np.full(nfeat, X.std()), np.full(NLATENT, Z.std())]).astype(np.float32)

    params = init_mlp(jax.random.PRNGKey(seed), [nfeat] + [HIDDEN['projector']] * 4 + [nfeat + NLATENT])
    opt = make_optimizer(lr, iters)
    opt_state = opt.init(params)
    JX, JZ = jnp.asarray(X), jnp.asarray(Z.astype(np.float32))
    Xsq = jnp.sum(JX ** 2, -1)
    mi, si, mo, so = map(jnp.asarray, (mean_in, std_in, mean_out, std_out))

    def loss_fn(params, query):
        dist = jnp.sum(query ** 2, -1, keepdims=True) - 2 * query @ JX.T + Xsq[None]
        ngh = jnp.argmin(dist, -1)
        Xg, Zg = JX[ngh], JZ[ngh]
        out = mlp(params, (query - mi) / si, jax.nn.relu) * so + mo
        Xt, Zt = out[:, :nfeat], out[:, nfeat:]
        Dg = jnp.sqrt(jnp.sum((Xg - query) ** 2, -1) + 1e-8)
        Dt = jnp.sqrt(jnp.sum((Xt - query) ** 2, -1) + 1e-8)
        return jnp.mean(1.0 * jnp.abs(Xg - Xt)) + jnp.mean(5.0 * jnp.abs(Zg - Zt)) + jnp.mean(0.3 * jnp.abs(Dg - Dt))

    @jax.jit
    def step(params, opt_state, query):
        loss, grads = jax.value_and_grad(loss_fn)(params, query)
        updates, opt_state = opt.update(grads, opt_state, params)
        return optax.apply_updates(params, updates), opt_state, loss

    def make_batch():
        i = rng.integers(0, len(X), batch)
        q = X[i].copy()
        sigma = rng.uniform(0, 1, (batch, 1))
        q[:, :traj0] += rng.normal(0, 1, (batch, traj0)) * pose_std * sigma
        # metade das consultas usa a trajetória do próprio frame (com ruído), metade uma trajetória do jogo
        own = rng.random(batch) < 0.4
        q[:, game_cols] = np.where(own[:, None],
                                   q[:, game_cols] + rng.normal(0, 0.3, (batch, len(game_cols))) * sigma,
                                   traj[rng.integers(0, len(traj), batch)])
        # às vezes a postura pedida é a outra (troca de arma)
        flip = rng.random(batch) < 0.15
        nst = sizes[groups.index('stance')]
        cols = slice(stance_col, stance_col + nst)
        onehot = (np.eye(nst)[rng.integers(0, nst, batch)] - off[cols]) / scale[cols]
        q[flip, cols] = onehot[flip]
        return q.astype(np.float32)

    params = run_loop('projector', step, params, opt_state, iters, make_batch)
    return params, dict(mean_in=mean_in, std_in=std_in, mean_out=mean_out, std_out=std_out)


# ---------------------------------------------------------------- main

def to_numpy(params):
    return [(np.asarray(w), np.asarray(b)) for w, b in params]


def save_mlp(path, layers, norm, **extra):
    out = {}
    for i, (w, b) in enumerate(layers):
        out[f'w{i}'] = w.astype(np.float32)
        out[f'b{i}'] = b.astype(np.float32)
    for k, v in norm.items():
        out[k] = np.asarray(v, np.float32)
    out.update(extra)
    np.savez(path, nlayers=len(layers), **out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('stage', nargs='?', default='all', choices=['decompressor', 'stepper', 'projector', 'all'])
    ap.add_argument('--iters', type=int, default=None)
    args = ap.parse_args()
    data = Data(load())
    print(f"banco: {len(data.X)} frames, {data.nb} ossos, {data.X.shape[1]} features; jax {jax.devices()}")

    if args.stage in ('decompressor', 'all'):
        params, Z = train_decompressor(data, args.iters or 80000)
        save_mlp(os.path.join(BUILD, 'lmm_decompressor.npz'), to_numpy(params['decompressor']),
                 dict(mean_in=np.zeros(data.X.shape[1] + NLATENT), std_in=np.ones(data.X.shape[1] + NLATENT),
                      mean_out=data.Dmean, std_out=data.Dstd))
        save_mlp(os.path.join(BUILD, 'lmm_compressor.npz'), to_numpy(params['compressor']),
                 dict(mean_in=data.Cmean, std_in=data.Cstd))
        np.save(os.path.join(BUILD, 'latent.npy'), Z.astype(np.float32))
    Z = np.load(os.path.join(BUILD, 'latent.npy'))
    if args.stage in ('stepper', 'all'):
        params, norm = train_stepper(data, Z, args.iters or 50000)
        save_mlp(os.path.join(BUILD, 'lmm_stepper.npz'), to_numpy(params), norm)
    if args.stage in ('projector', 'all'):
        params, norm = train_projector(data, Z, args.iters or 80000)
        save_mlp(os.path.join(BUILD, 'lmm_projector.npz'), to_numpy(params), norm)


if __name__ == '__main__':
    main()
