"""Leitor mínimo de FBX binário (7.x) e avaliação da hierarquia/animação.

Usado para extrair as poses das animações da ExplosiveLLC direto dos arquivos .FBX, no mesmo
espaço que a Unity usa depois da importação (X espelhado, centímetros -> metros).
"""
import struct, zlib
import numpy as np
from collections import defaultdict

class Node:
    __slots__ = ('name', 'props', 'children')
    def __init__(self, name, props, children):
        self.name, self.props, self.children = name, props, children
    def find(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None
    def findall(self, name):
        return [c for c in self.children if c.name == name]
    def __repr__(self):
        return f"Node({self.name}, {self.props[:3]}, {len(self.children)} filhos)"

_ARR = {'f': ('f', 4), 'd': ('d', 8), 'l': ('q', 8), 'i': ('i', 4), 'b': ('b', 1)}

def _read_prop(data, o):
    t = chr(data[o]); o += 1
    if t == 'Y': return struct.unpack_from('<h', data, o)[0], o + 2
    if t == 'C': return bool(data[o]), o + 1
    if t == 'I': return struct.unpack_from('<i', data, o)[0], o + 4
    if t == 'F': return struct.unpack_from('<f', data, o)[0], o + 4
    if t == 'D': return struct.unpack_from('<d', data, o)[0], o + 8
    if t == 'L': return struct.unpack_from('<q', data, o)[0], o + 8
    if t in _ARR:
        n, enc, clen = struct.unpack_from('<III', data, o); o += 12
        raw = data[o:o + clen]; o += clen
        if enc == 1:
            raw = zlib.decompress(raw)
        fmt, size = _ARR[t]
        return list(struct.unpack('<%d%s' % (n, fmt), raw[:n * size])), o
    if t in 'SR':
        n = struct.unpack_from('<I', data, o)[0]; o += 4
        raw = data[o:o + n]; o += n
        if t == 'S':
            return raw.decode('utf-8', 'replace'), o
        return raw, o
    raise ValueError('tipo de propriedade desconhecido %r em %d' % (t, o - 1))

def _read_node(data, o, wide):
    if wide:
        end, nprops, plen = struct.unpack_from('<QQQ', data, o); o += 24
    else:
        end, nprops, plen = struct.unpack_from('<III', data, o); o += 12
    nlen = data[o]; o += 1
    if end == 0:
        return None, o
    name = data[o:o + nlen].decode('ascii', 'replace'); o += nlen
    props = []
    for _ in range(nprops):
        p, o = _read_prop(data, o)
        props.append(p)
    children = []
    sentinel = 25 if wide else 13
    while o < end - sentinel or (o < end and end - o > sentinel):
        c, o2 = _read_node(data, o, wide)
        if c is None:
            o = o2
            break
        children.append(c)
        o = o2
    return Node(name, props, children), end

def read(path):
    data = open(path, 'rb').read()
    assert data[:20] == b'Kaydara FBX Binary  ', 'não é FBX binário'
    version = struct.unpack_from('<I', data, 23)[0]
    wide = version >= 7500
    o = 27
    top = []
    while o < len(data):
        n, o2 = _read_node(data, o, wide)
        if n is None:
            break
        top.append(n)
        o = o2
    return Node('<root>', [version], top)

def props70(node):
    """Dicionário nome -> valores de um bloco Properties70."""
    out = {}
    p = node.find('Properties70')
    if p:
        for P in p.findall('P'):
            out[P.props[0]] = P.props[4:]
    return out


# ---- Avaliação da cena (convenção de vetor-coluna) ----

KTIME = 46186158000  # unidades de tempo FBX por segundo

def euler(deg, order=0):
    x, y, z = np.radians(np.asarray(deg, float))
    cx, sx, cy, sy, cz, sz = np.cos(x), np.sin(x), np.cos(y), np.sin(y), np.cos(z), np.sin(z)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    mats = {'X': Rx, 'Y': Ry, 'Z': Rz}
    seq = ['XYZ', 'XZY', 'YZX', 'YXZ', 'ZXY', 'ZYX'][order]
    R = np.eye(3)
    for axis in seq:          # primeiro eixo da sequência é aplicado primeiro
        R = mats[axis] @ R
    return R

def m4(R=None, t=None, s=None):
    M = np.eye(4)
    if R is not None: M[:3, :3] = R
    if s is not None: M[:3, :3] = M[:3, :3] @ np.diag(s)
    if t is not None: M[:3, 3] = t
    return M

class Scene:
    def __init__(self, path):
        r = read(path)
        self.objs = {n.props[0]: n for n in r.find('Objects').children}
        self.oo, self.op = [], []
        for c in r.find('Connections').children:
            if c.props[0] == 'OO': self.oo.append((c.props[1], c.props[2]))
            elif c.props[0] == 'OP': self.op.append((c.props[1], c.props[2], c.props[3]))
        self.models = {i: n for i, n in self.objs.items() if n.name == 'Model'}
        self.name = {i: n.props[1].split('\x00')[0] for i, n in self.models.items()}
        self.by_name = {v: k for k, v in self.name.items()}
        self.parent = {i: None for i in self.models}
        for ch, pa in self.oo:
            if ch in self.models and pa in self.models:
                self.parent[ch] = pa
        self.props = {i: props70(n) for i, n in self.models.items()}
        self.anim = defaultdict(dict)   # nome -> {'T'/'R'/'S': {eixo: (tempos, valores)}}
        self._load_anim()

    def _load_anim(self):
        curves = {i: n for i, n in self.objs.items() if n.name == 'AnimationCurve'}
        nodes = {i: n for i, n in self.objs.items() if n.name == 'AnimationCurveNode'}
        node_target = {}
        for ch, pa, prop in self.op:
            if ch in nodes and pa in self.models:
                node_target[ch] = (self.name[pa], {'Lcl Translation': 'T', 'Lcl Rotation': 'R', 'Lcl Scaling': 'S'}.get(prop))
        for ch, pa, prop in self.op:
            if ch in curves and pa in node_target:
                bone, chan = node_target[pa]
                if chan is None: continue
                cn = curves[ch]
                t = np.array(cn.find('KeyTime').props[0], dtype=np.float64) / KTIME
                v = np.array(cn.find('KeyValueFloat').props[0], dtype=np.float64)
                self.anim[bone].setdefault(chan, {})[prop[-1]] = (t, v)

    def duration(self):
        end = 0
        for chans in self.anim.values():
            for axes in chans.values():
                for t, _ in axes.values():
                    end = max(end, t[-1])
        return end

    def local(self, i, time=None, override=None):
        p = self.props[i]
        nm = self.name[i]
        T = np.array(p.get('Lcl Translation', [0, 0, 0]), float)
        R = np.array(p.get('Lcl Rotation', [0, 0, 0]), float)
        S = np.array(p.get('Lcl Scaling', [1, 1, 1]), float)
        src = override.anim.get(nm) if override is not None else self.anim.get(nm)
        if time is not None and src:
            for chan, vec in (('T', T), ('R', R), ('S', S)):
                for k, ax in enumerate('XYZ'):
                    if chan in src and ax in src[chan]:
                        t, v = src[chan][ax]
                        vec[k] = np.interp(time, t, v)
        order = int(p.get('RotationOrder', [0])[0])
        Rpre = euler(p.get('PreRotation', [0, 0, 0]))
        Rpost = euler(p.get('PostRotation', [0, 0, 0]))
        Rp = np.array(p.get('RotationPivot', [0, 0, 0]), float)
        Ro = np.array(p.get('RotationOffset', [0, 0, 0]), float)
        Sp = np.array(p.get('ScalingPivot', [0, 0, 0]), float)
        So = np.array(p.get('ScalingOffset', [0, 0, 0]), float)
        M = m4(t=T) @ m4(t=Ro) @ m4(t=Rp) @ m4(Rpre) @ m4(euler(R, order)) @ m4(Rpost.T) @ m4(t=-Rp) \
            @ m4(t=So) @ m4(t=Sp) @ m4(s=S) @ m4(t=-Sp)
        return M

    def world(self, time=None, override=None):
        cache = {}
        def w(i):
            if i in cache: return cache[i]
            pa = self.parent.get(i)
            M = self.local(i, time, override)
            if pa is not None:
                M = w(pa) @ M
            cache[i] = M
            return M
        return {self.name[i]: w(i) for i in self.models}

FLIP = np.diag([-1.0, 1.0, 1.0])

def to_unity(M, scale=0.01):
    """Matriz rígida FBX (cm, mão direita) -> Unity (m, mão esquerda)."""
    U = np.eye(4)
    U[:3, :3] = FLIP @ M[:3, :3] @ FLIP
    U[:3, 3] = FLIP @ M[:3, 3] * scale
    return U

def pts_to_unity(P, scale=0.01):
    return (P @ FLIP.T) * scale
