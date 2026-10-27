"""Leitura e escrita mínimas de FBX binário (7.x) e avaliação da hierarquia/animação.

Usado para extrair as poses das animações da ExplosiveLLC direto dos arquivos .FBX, no mesmo
espaço que a Unity usa depois da importação (X espelhado, centímetros -> metros), e para gravar
animações novas no mesmo esqueleto (clonando um FBX existente e trocando as curvas).
"""
import struct, zlib
import numpy as np
from collections import defaultdict

class Node:
    __slots__ = ('name', 'props', 'children', 'ptypes')
    def __init__(self, name, props, children, ptypes=None):
        self.name, self.props, self.children = name, props, children
        self.ptypes = ptypes if ptypes is not None else [None] * len(props)
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
    v, o2 = _read_prop_value(data, o)
    return v, o2, chr(data[o])

def _read_prop_value(data, o):
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
            try:
                return raw.decode('utf-8'), o
            except UnicodeDecodeError:
                return raw, o
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
    props, ptypes = [], []
    for _ in range(nprops):
        p, o, t = _read_prop(data, o)
        props.append(p)
        ptypes.append(t)
    children = []
    sentinel = 25 if wide else 13
    while o < end - sentinel or (o < end and end - o > sentinel):
        c, o2 = _read_node(data, o, wide)
        if c is None:
            o = o2
            break
        children.append(c)
        o = o2
    return Node(name, props, children, ptypes), end

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

# ---- Escrita ----

_FOOT_ID = b'\xfa\xbc\xab\x09\xd0\xc8\xd4\x66\xb1\x76\xfb\x83\x1c\xf7\x26\x7e'
_FOOT_MAGIC = b'\xf8\x5a\x8c\x6a\xde\xf5\xd9\x7e\xec\xe9\x0c\xe3\x75\x8f\x29\x0b'

def _encode_prop(t, v):
    if t == 'Y': return b'Y' + struct.pack('<h', v)
    if t == 'C': return b'C' + bytes([1 if v else 0])
    if t == 'I': return b'I' + struct.pack('<i', v)
    if t == 'F': return b'F' + struct.pack('<f', v)
    if t == 'D': return b'D' + struct.pack('<d', v)
    if t == 'L': return b'L' + struct.pack('<q', v)
    if t in _ARR:
        fmt, size = _ARR[t]
        raw = struct.pack('<%d%s' % (len(v), fmt), *v)
        enc = 0
        if len(raw) > 128:
            raw = zlib.compress(raw)
            enc = 1
        return t.encode() + struct.pack('<III', len(v), enc, len(raw)) + raw
    if t in 'SR':
        raw = v if isinstance(v, (bytes, bytearray)) else str(v).encode('utf-8')
        return t.encode() + struct.pack('<I', len(raw)) + raw
    raise ValueError('tipo de propriedade desconhecido %r' % t)

def _encode_node(node, offset, wide, is_last=False):
    """Bytes do nó começando em 'offset' (o deslocamento final é absoluto no arquivo)."""
    props = b''.join(_encode_prop(t, v) for t, v in zip(node.ptypes, node.props))
    name = node.name.encode('ascii')
    head = 25 if wide else 13
    sentinel = b'\0' * head
    body_start = offset + head + len(name) + len(props)
    body = b''
    pos = body_start
    for k, c in enumerate(node.children):
        b = _encode_node(c, pos, wide, k == len(node.children) - 1)
        body += b
        pos += len(b)
    if node.children or (not node.props and not is_last):
        body += sentinel
        pos += len(sentinel)
    fmt = '<QQQ' if wide else '<III'
    return struct.pack(fmt, pos, len(node.ptypes), len(props)) + bytes([len(name)]) + name + props + body

def write(path, root):
    """Grava a árvore (root.props[0] = versão) como FBX binário."""
    version = root.props[0]
    wide = version >= 7500
    out = bytearray(b'Kaydara FBX Binary  \x00\x1a\x00' + struct.pack('<I', version))
    for k, c in enumerate(root.children):
        out += _encode_node(c, len(out), wide, k == len(root.children) - 1)
    out += b'\0' * (25 if wide else 13)
    out += _FOOT_ID + b'\0' * 4
    pad = ((len(out) + 15) & ~15) - len(out)
    out += b'\0' * (pad if pad else 16)
    out += struct.pack('<I', version) + b'\0' * 120 + _FOOT_MAGIC
    with open(path, 'wb') as f:
        f.write(out)

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
