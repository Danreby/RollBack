"""Quaternions em numpy no formato (x, y, z, w), com a mesma álgebra da Quaternion da Unity
(produto de Hamilton, q * v = q v q^-1). Todas as funções aceitam arrays com eixos extras na frente."""
import numpy as np


def normalize(q, eps=1e-8):
    return q / (np.linalg.norm(q, axis=-1, keepdims=True) + eps)


def abs(q):
    """Hemisfério com w >= 0 (mesma rotação, caminho mais curto)."""
    return np.where(q[..., 3:4] < 0, -q, q)


def inv(q):
    return np.concatenate([-q[..., :3], q[..., 3:]], axis=-1)


def mul(a, b):
    ax, ay, az, aw = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz], axis=-1)


def mul_vec(q, v):
    t = 2.0 * np.cross(q[..., :3], v)
    return v + q[..., 3:4] * t + np.cross(q[..., :3], t)


def inv_mul(a, b):
    return mul(inv(a), b)


def mul_inv(a, b):
    return mul(a, inv(b))


def inv_mul_vec(q, v):
    return mul_vec(inv(q), v)


def exp(v, eps=1e-8):
    half = np.linalg.norm(v, axis=-1, keepdims=True)
    c = np.cos(half)
    s = np.where(half < eps, 1.0, np.sin(half) / np.maximum(half, eps))
    return np.concatenate([v * s, c], axis=-1)


def log(q, eps=1e-8):
    length = np.linalg.norm(q[..., :3], axis=-1, keepdims=True)
    halfangle = np.arctan2(length, q[..., 3:4])
    return np.where(length < eps, q[..., :3], q[..., :3] * halfangle / np.maximum(length, eps))


def to_scaled_angle_axis(q):
    return 2.0 * log(q)


def from_scaled_angle_axis(v):
    return exp(v / 2.0)


def slerp(a, b, t):
    """Interpolação esférica pelo caminho mais curto; t com shape [..., 1] ou escalar."""
    t = np.asarray(t, dtype=np.float64)
    b = np.where(np.sum(a * b, axis=-1, keepdims=True) < 0, -b, b)
    delta = to_scaled_angle_axis(abs(mul_inv(b, a)))
    return mul(from_scaled_angle_axis(delta * t), a)


def to_matrix(q):
    q = normalize(q)
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return np.stack([
        np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], axis=-1),
        np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], axis=-1),
        np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], axis=-1)], axis=-2)


def from_matrix(R):
    R = np.asarray(R, dtype=np.float64)
    out = np.empty(R.shape[:-2] + (4,))
    flat_R = R.reshape(-1, 3, 3)
    flat_q = out.reshape(-1, 4)
    for i, m in enumerate(flat_R):
        t = m[0, 0] + m[1, 1] + m[2, 2]
        if t > 0:
            s = np.sqrt(t + 1.0) * 2
            q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, s / 4]
        elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
            s = np.sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
            q = [s / 4, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
        elif m[1, 1] > m[2, 2]:
            s = np.sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
            q = [(m[0, 1] + m[1, 0]) / s, s / 4, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
        else:
            s = np.sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
            q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, s / 4, (m[1, 0] - m[0, 1]) / s]
        flat_q[i] = q
    return normalize(out)


def to_xform_xy(q):
    """Duas primeiras colunas da matriz de rotação (representação contínua de 6 números)."""
    return to_matrix(q)[..., :2]


def from_xform_xy(x):
    c0 = x[..., 0] / np.linalg.norm(x[..., 0], axis=-1, keepdims=True)
    c2 = np.cross(c0, x[..., 1])
    c2 /= np.linalg.norm(c2, axis=-1, keepdims=True)
    c1 = np.cross(c2, c0)
    return from_matrix(np.stack([c0, c1, c2], axis=-1))


def fk(lrot, lpos, parents):
    """Cinemática direta: rotações/posições locais [..., ossos, 4/3] -> globais."""
    grot, gpos = [lrot[..., 0, :]], [lpos[..., 0, :]]
    for i in range(1, len(parents)):
        p = parents[i]
        gpos.append(mul_vec(grot[p], lpos[..., i, :]) + gpos[p])
        grot.append(mul(grot[p], lrot[..., i, :]))
    return np.stack(grot, axis=-2), np.stack(gpos, axis=-2)


def fk_vel(lrot, lpos, lvel, lang, parents):
    """FK incluindo velocidades lineares e angulares (espaço do mundo)."""
    grot, gpos, gvel, gang = [lrot[..., 0, :]], [lpos[..., 0, :]], [lvel[..., 0, :]], [lang[..., 0, :]]
    for i in range(1, len(parents)):
        p = parents[i]
        gpos.append(mul_vec(grot[p], lpos[..., i, :]) + gpos[p])
        grot.append(mul(grot[p], lrot[..., i, :]))
        gvel.append(mul_vec(grot[p], lvel[..., i, :]) + np.cross(gang[p], mul_vec(grot[p], lpos[..., i, :])) + gvel[p])
        gang.append(mul_vec(grot[p], lang[..., i, :]) + gang[p])
    return (np.stack(grot, axis=-2), np.stack(gpos, axis=-2),
            np.stack(gvel, axis=-2), np.stack(gang, axis=-2))
