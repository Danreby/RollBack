"""Leitor mínimo de glTF binário (.glb): nós, esqueleto e animações (TRS por canal)."""
import json
import struct
import numpy as np

_COMP = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
_SIZE = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


class Glb:
    def __init__(self, path):
        data = open(path, 'rb').read()
        assert data[:4] == b'glTF'
        jlen = struct.unpack_from('<I', data, 12)[0]
        self.json = json.loads(data[20:20 + jlen])
        o = 20 + jlen
        blen = struct.unpack_from('<I', data, o)[0]
        self.bin = data[o + 8:o + 8 + blen]
        self.nodes = self.json['nodes']
        self.names = [n.get('name', f'node{i}') for i, n in enumerate(self.nodes)]
        self.parent = [-1] * len(self.nodes)
        for i, n in enumerate(self.nodes):
            for c in n.get('children', []):
                self.parent[c] = i
        self.animations = {a['name']: a for a in self.json.get('animations', [])}

    def accessor(self, idx):
        acc = self.json['accessors'][idx]
        view = self.json['bufferViews'][acc['bufferView']]
        dtype = _COMP[acc['componentType']]
        n = _SIZE[acc['type']]
        off = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
        stride = view.get('byteStride', 0)
        count = acc['count']
        if stride and stride != n * np.dtype(dtype).itemsize:
            raw = np.frombuffer(self.bin, dtype=np.uint8, count=stride * count, offset=off).reshape(count, stride)
            arr = raw[:, :n * np.dtype(dtype).itemsize].copy().view(dtype).reshape(count, n)
        else:
            arr = np.frombuffer(self.bin, dtype=dtype, count=count * n, offset=off).reshape(count, n)
        return arr.astype(np.float64)

    def rest(self):
        """TRS de repouso de cada nó (x, y, z, w para rotações)."""
        T = np.zeros((len(self.nodes), 3)); R = np.zeros((len(self.nodes), 4)); S = np.ones((len(self.nodes), 3))
        R[:, 3] = 1
        for i, n in enumerate(self.nodes):
            if 'translation' in n: T[i] = n['translation']
            if 'rotation' in n: R[i] = n['rotation']
            if 'scale' in n: S[i] = n['scale']
        return T, R, S

    def sample(self, name, times):
        """TRS locais de todos os nós nos instantes dados (interpolação linear / slerp)."""
        import quat
        T, R, S = self.rest()
        n = len(times)
        T = np.repeat(T[None], n, 0); R = np.repeat(R[None], n, 0); S = np.repeat(S[None], n, 0)
        a = self.animations[name]
        for ch in a['channels']:
            node = ch['target'].get('node')
            path = ch['target']['path']
            smp = a['samplers'][ch['sampler']]
            t = self.accessor(smp['input'])[:, 0]
            v = self.accessor(smp['output'])
            if smp.get('interpolation', 'LINEAR') == 'CUBICSPLINE':
                v = v.reshape(len(t), 3, -1)[:, 1]
            k = np.clip(np.searchsorted(t, times, side='right') - 1, 0, len(t) - 1)
            k1 = np.clip(k + 1, 0, len(t) - 1)
            span = np.maximum(t[k1] - t[k], 1e-9)
            u = np.clip((times - t[k]) / span, 0, 1)[:, None]
            if smp.get('interpolation', 'LINEAR') == 'STEP':
                u = u * 0
            if path == 'rotation':
                R[:, node] = quat.slerp(v[k], v[k1], u)
            elif path == 'translation':
                T[:, node] = v[k] * (1 - u) + v[k1] * u
            elif path == 'scale':
                S[:, node] = v[k] * (1 - u) + v[k1] * u
        return T, R, S

    def duration(self, name):
        a = self.animations[name]
        return max(self.json['accessors'][s['input']]['max'][0] for s in a['samplers'])
