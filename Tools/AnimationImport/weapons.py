"""Converte as armas novas para malhas lidas em tempo de execução (Assets/Weapons).

Armas:
  - Adaga e Espadão: "LowPoly Medieval Weapons" da Quaternius (CC0), Dagger.fbx e Sword_Big.fbx;
  - Espada longa: 2Hand-Sword.FBX do pacote gratuito da ExplosiveLLC (já no projeto).

Cada malha é gravada num .bytes simples (lido por Assets/Scripts/Combat/WeaponMesh.cs) no "espaço
da pegada": origem no centro do punho fechado, +Y = do pomo para a ponta, X = direção do fio.
Assim a posição/rotação da arma na mão é a mesma para todas: a pegada da espada da ExplosiveLLC
(o Animator fecha os dedos da mão direita com essa pose, camada RightHandGrip).

As cores dos materiais da Quaternius (lineares no FBX) viram materiais URP/Lit em sRGB.

Uso:  python3 weapons.py
"""
import os
import sys
import json
import struct
import hashlib
import subprocess
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'MotionMatching'))
import fbx  # noqa: E402

PROJECT = os.path.abspath(os.path.join(HERE, '..', '..'))
SOURCE = os.path.join(HERE, 'source')
OUT = os.path.join(PROJECT, 'Assets', 'Weapons')
SWORD_2H = os.path.join(PROJECT, 'Assets', 'ExplosiveLLC', 'RPG Character Mecanim Animation Pack FREE',
                        'Models', 'Weapons', '2Hand-Sword.FBX')
DOWNLOADS = {
    'Dagger.fbx': 'https://raw.githubusercontent.com/Arod231/monomachia/master/game/assets/weapons/Dagger.fbx',
    'Sword_Big.fbx': 'https://raw.githubusercontent.com/Arod231/monomachia/master/game/assets/weapons/Sword_Big.fbx',
}

# Pegada da espada da ExplosiveLLC no espaço da B_R_Hand (Unity, metros): centro do punho e
# direção do cabo (pomo -> lâmina). O fio fica ao longo do X da mão, que é para onde os nós dos
# dedos apontam e para onde a ponta corre nos golpes da Quaternius (medido nas animações).
FIST = np.array([-0.1723, 0.0178, -0.0069])
AXIS = np.array([-0.0039, -0.0803, 0.9968])
# Do centro do punho até a guarda (a mão encosta na guarda, como na espada da ExplosiveLLC)
FIST_TO_GUARD = 0.088

# Quaternius: comprimento total desejado (m) e onde fica a guarda no arquivo (coordenada Z da base)
QUATERNIUS = {
    'Adaga': dict(file='Dagger.fbx', length=0.85, guard_z=0.154),
    'Espadao': dict(file='Sword_Big.fbx', length=2.6, guard_z=1.132),
}

# Cores (lineares no FBX); metálico/suavidade escolhidos por material
MATERIALS = {
    'Steel': (0.6, 0.62), 'LightSteel': (0.6, 0.7), 'DarkSteel': (0.5, 0.5),
    'Gold': (0.85, 0.6), 'LightWood': (0.0, 0.25), 'DarkWood': (0.0, 0.2),
}
LIT_SHADER = '933532a4fcc9baf4fa0491de14d08ed7'
RPG_MATERIAL = 'Assets/ExplosiveLLC/RPG Character Mecanim Animation Pack FREE/Materials/RPG-Character.mat'


def guid_for(name):
    return hashlib.md5(('rollback-weapon-' + name).encode()).hexdigest()


def download():
    os.makedirs(SOURCE, exist_ok=True)
    for name, url in DOWNLOADS.items():
        path = os.path.join(SOURCE, name)
        if not os.path.exists(path):
            print('baixando', name)
            subprocess.check_call(['curl', '-sSfL', '-o', path, url])


def polygons(index_array):
    faces, cur = [], []
    for i in index_array:
        if i < 0:
            cur.append(~i)
            faces.append(cur)
            cur = []
        else:
            cur.append(i)
    return faces


def layer_values(geo, layer, values_name, index_name, width):
    """Valores por canto de polígono (ByPolygonVertex), resolvendo IndexToDirect."""
    node = geo.find(layer)
    if node is None:
        return None
    vals = np.array(node.find(values_name).props[0]).reshape(-1, width)
    mapping = node.find('MappingInformationType').props[0]
    ref = node.find('ReferenceInformationType').props[0]
    assert mapping == 'ByPolygonVertex', mapping
    if ref == 'IndexToDirect':
        vals = vals[np.array(node.find(index_name).props[0])]
    return vals


def read_geometry(path):
    root = fbx.read(path)
    objs = root.find('Objects')
    geo = [n for n in objs.children if n.name == 'Geometry'][0]
    model = [n for n in objs.children if n.name == 'Model'][0]
    v = np.array(geo.find('Vertices').props[0], dtype=np.float64).reshape(-1, 3)
    faces = polygons(geo.find('PolygonVertexIndex').props[0])
    normals = layer_values(geo, 'LayerElementNormal', 'Normals', 'NormalsIndex', 3)
    uvs = layer_values(geo, 'LayerElementUV', 'UV', 'UVIndex', 2)
    mat_node = geo.find('LayerElementMaterial')
    mats = np.array(mat_node.find('Materials').props[0]) if mat_node is not None else np.zeros(1, int)
    if len(mats) != len(faces):
        mats = np.full(len(faces), mats[0])
    # materiais na ordem em que estão ligados ao modelo
    names = {n.props[0]: n.props[1].split('\x00')[0] for n in objs.children if n.name == 'Material'}
    order = [c.props[1] for c in root.find('Connections').children
             if c.props[0] == 'OO' and c.props[2] == model.props[0] and c.props[1] in names]
    mat_names = [names[i] for i in order]
    colors = {}
    for n in objs.children:
        if n.name == 'Material':
            colors[n.props[1].split('\x00')[0]] = fbx.props70(n).get('DiffuseColor', [0.8, 0.8, 0.8])
    return dict(v=v, faces=faces, normals=normals, uvs=uvs, mats=mats, mat_names=mat_names,
                colors=colors, model=fbx.props70(model))


def triangulate(geo, to_canonical, rotate_normal, flat):
    """Triângulos por material, já no espaço da pegada (Unity: X espelhado, ordem invertida)."""
    pos, nor, uv = [], [], []
    by_mat = {}
    corner = 0
    for f, m in zip(geo['faces'], geo['mats']):
        corners = list(range(corner, corner + len(f)))
        corner += len(f)
        for k in range(1, len(f) - 1):
            tri = [0, k + 1, k]                      # ordem invertida: espelhar X troca o sentido
            idx = []
            pts = to_canonical(geo['v'][[f[t] for t in tri]])
            n_flat = np.cross(pts[1] - pts[0], pts[2] - pts[0])
            n_flat /= max(np.linalg.norm(n_flat), 1e-12)
            for j, t in enumerate(tri):
                pos.append(pts[j])
                if flat or geo['normals'] is None:
                    nor.append(n_flat)
                else:
                    nor.append(rotate_normal(geo['normals'][corners[t]]))
                uv.append(geo['uvs'][corners[t]] if geo['uvs'] is not None else (0.0, 0.0))
                idx.append(len(pos) - 1)
            by_mat.setdefault(int(m), []).extend(idx)
    return np.array(pos), np.array(nor), np.array(uv), by_mat


def write_mesh(name, pos, nor, uv, submeshes):
    path = os.path.join(OUT, 'Meshes', name + '.bytes')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(b'WMSH')
        f.write(struct.pack('<iii', 1, len(pos), len(submeshes)))
        f.write(pos.astype('<f4').tobytes())
        f.write(nor.astype('<f4').tobytes())
        f.write(uv.astype('<f4').tobytes())
        for idx in submeshes:
            f.write(struct.pack('<i', len(idx)))
            f.write(np.asarray(idx, dtype='<i4').tobytes())
    write_meta(path, guid_for(name + '.bytes'),
               "TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


def write_meta(path, guid, body):
    with open(path + '.meta', 'w') as f:
        f.write(f"fileFormatVersion: 2\nguid: {guid}\n{body}")


def folder_meta(path):
    if not os.path.exists(path + '.meta'):
        rel = os.path.relpath(path, PROJECT)
        write_meta(path, guid_for('folder:' + rel),
                   "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


def srgb(c):
    c = np.clip(np.asarray(c, float), 0, 1)
    return np.where(c <= 0.0031308, 12.92 * c, 1.055 * c ** (1 / 2.4) - 0.055)


def write_material(name, color, metallic, smoothness):
    path = os.path.join(OUT, 'Materials', name + '.mat')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    r, g, b = srgb(color)
    tex = ''.join(f"    - {t}:\n        m_Texture: {{fileID: 0}}\n        m_Scale: {{x: 1, y: 1}}\n        m_Offset: {{x: 0, y: 0}}\n"
                  for t in ('_BaseMap', '_BumpMap', '_DetailAlbedoMap', '_DetailMask', '_DetailNormalMap',
                            '_EmissionMap', '_MainTex', '_MetallicGlossMap', '_OcclusionMap', '_ParallaxMap',
                            '_SpecGlossMap'))
    floats = {'_AddPrecomputedVelocity': 0, '_AlphaClip': 0, '_AlphaToMask': 0, '_Blend': 0,
              '_BlendModePreserveSpecular': 1, '_BumpScale': 1, '_ClearCoatMask': 0, '_ClearCoatSmoothness': 0,
              '_Cull': 2, '_Cutoff': 0.5, '_DetailAlbedoMapScale': 1, '_DetailNormalMapScale': 1, '_DstBlend': 0,
              '_DstBlendAlpha': 0, '_EnvironmentReflections': 1, '_GlossMapScale': 0, '_Glossiness': 0,
              '_GlossyReflections': 0, '_Metallic': metallic, '_OcclusionStrength': 1, '_Parallax': 0.005,
              '_QueueOffset': 0, '_ReceiveShadows': 1, '_Smoothness': smoothness, '_SmoothnessTextureChannel': 0,
              '_SpecularHighlights': 1, '_SrcBlend': 1, '_SrcBlendAlpha': 1, '_Surface': 0, '_WorkflowMode': 1,
              '_ZWrite': 1}
    fl = ''.join(f"    - {k}: {v}\n" for k, v in floats.items())
    col = f"{{r: {r:.4f}, g: {g:.4f}, b: {b:.4f}, a: 1}}"
    text = f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Shader: {{fileID: 4800000, guid: {LIT_SHADER}, type: 3}}
  m_Parent: {{fileID: 0}}
  m_ModifiedSerializedProperties: 0
  m_ValidKeywords: []
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 0
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap:
    RenderType: Opaque
  disabledShaderPasses:
  - MOTIONVECTORS
  m_LockedProperties:
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
{tex}    m_Ints: []
    m_Floats:
{fl}    m_Colors:
    - _BaseColor: {col}
    - _Color: {col}
    - _EmissionColor: {{r: 0, g: 0, b: 0, a: 1}}
    - _SpecColor: {{r: 0.19999996, g: 0.19999996, b: 0.19999996, a: 1}}
  m_BuildTextureStacks: []
  m_AllowLocking: 1
"""
    with open(path, 'w') as f:
        f.write(text)
    guid = guid_for(name + '.mat')
    write_meta(path, guid, "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 2100000\n"
                           "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    return guid


def unity_euler(R):
    """Ângulos de Quaternion.Euler (a Unity aplica Z, depois X, depois Y: R = Ry Rx Rz)."""
    x = np.degrees(np.arcsin(np.clip(-R[1, 2], -1, 1)))
    y = np.degrees(np.arctan2(R[0, 2], R[2, 2]))
    z = np.degrees(np.arctan2(R[1, 0], R[1, 1]))
    return np.round(np.array([x, y, z]) % 360, 3)


def grip_frame(axis, width_hint=np.array([1.0, 0, 0])):
    y = axis / np.linalg.norm(axis)
    x = width_hint - (width_hint @ y) * y
    x /= np.linalg.norm(x)
    return np.stack([x, y, np.cross(x, y)], axis=1)       # colunas: fio, lâmina, face


def blade_extent(pos, guard):
    """Base (logo acima da guarda) e ponta da lâmina, no eixo +Y."""
    tip = pos[:, 1].max()
    return np.array([0.0, guard + 0.04, 0.0]), np.array([0.0, tip - 0.02, 0.0])


def convert_quaternius(name, spec, mat_guids):
    geo = read_geometry(os.path.join(SOURCE, spec['file']))
    v = geo['v']
    total = v[:, 2].max() - v[:, 2].min()
    s = spec['length'] / total
    guard = spec['guard_z'] * s
    grip_y = guard - FIST_TO_GUARD

    # Arquivo (Blender, Z = lâmina) -> Unity (o importador gira -90 em X e espelha X): (-x, z, -y)
    def to_canonical(p):
        u = np.stack([-p[:, 0], p[:, 2], -p[:, 1]], axis=1) * s
        u[:, 1] -= grip_y
        return u

    pos, nor, uv, by_mat = triangulate(geo, to_canonical, None, flat=True)
    order = sorted(by_mat)
    write_mesh(name, pos, nor, uv, [by_mat[m] for m in order])
    materials = [geo['mat_names'][m] for m in order]
    base, tip = blade_extent(pos, guard - grip_y)
    return dict(materials=[mat_guids[m] for m in materials], material_names=materials,
                bladeBase=base.tolist(), bladeTip=tip.tolist(),
                length=float(pos[:, 1].max() - pos[:, 1].min()),
                radius=float(np.abs(pos[:, [0, 2]]).max()), minY=float(pos[:, 1].min()), maxY=float(pos[:, 1].max()))


def convert_2h():
    geo = read_geometry(SWORD_2H)
    p = geo['model']
    gr = fbx.euler(p.get('GeometricRotation', [0, 0, 0]))
    gt = np.array(p.get('GeometricTranslation', [0, 0, 0]))
    gs = np.array(p.get('GeometricScaling', [1, 1, 1]))
    F = np.diag([-1.0, 1.0, 1.0])
    # A malha (com o pivô geométrico) já está no espaço da mão quando a espada é filha dela
    R = grip_frame(AXIS)

    def to_canonical(q):
        hand = (F @ (gr @ (q * gs).T + gt[:, None])).T * 0.01
        return (hand - FIST) @ R            # R^T (p - punho), linha a linha

    def rotate_normal(n):
        h = F @ (gr @ np.asarray(n, float))
        c = h @ R
        return c / max(np.linalg.norm(c), 1e-12)

    pos, nor, uv, by_mat = triangulate(geo, to_canonical, rotate_normal, flat=False)
    write_mesh('EspadaLonga', pos, nor, uv, [by_mat[m] for m in sorted(by_mat)])
    # a guarda é onde a seção fica mais larga perto do punho
    ys = np.linspace(0.0, 0.4, 81)
    widths = [np.abs(pos[(pos[:, 1] >= a) & (pos[:, 1] < a + 0.005), 0]).max(initial=0) for a in ys]
    guard = ys[int(np.argmax(widths))]
    base, tip = blade_extent(pos, guard)
    return dict(materials=[RPG_MATERIAL], bladeBase=base.tolist(), bladeTip=tip.tolist(),
                length=float(pos[:, 1].max() - pos[:, 1].min()),
                radius=float(np.abs(pos[:, [0, 2]]).max()), minY=float(pos[:, 1].min()), maxY=float(pos[:, 1].max()))


def main():
    download()
    for folder in (OUT, os.path.join(OUT, 'Meshes'), os.path.join(OUT, 'Materials')):
        os.makedirs(folder, exist_ok=True)
        folder_meta(folder)
    colors = read_geometry(os.path.join(SOURCE, 'Dagger.fbx'))['colors']
    mat_guids = {name: write_material(name, colors[name], *MATERIALS[name]) for name in MATERIALS}

    specs = {name: convert_quaternius(name, spec, mat_guids) for name, spec in QUATERNIUS.items()}
    specs['EspadaLonga'] = convert_2h()

    # Pegada: rotação/posição da arma na mão direita (e na esquerda, espelhada, para a segunda adaga)
    R = grip_frame(AXIS)
    M = np.diag([1.0, 1.0, -1.0])
    grip = dict(right=dict(position=FIST.round(4).tolist(), euler=unity_euler(R).tolist()),
                left=dict(position=(M @ FIST).round(4).tolist(), euler=unity_euler(M @ R @ M).tolist()))
    with open(os.path.join(HERE, 'build_weapons.json'), 'w') as f:
        json.dump(dict(grip=grip, weapons=specs, mesh_guids={k: guid_for(k + '.bytes') for k in specs}), f, indent=1)
    for name, spec in specs.items():
        print(f"{name:12s} comprimento {spec['length']:.2f} m  lâmina {spec['bladeBase'][1]:.2f}..{spec['bladeTip'][1]:.2f}"
              f"  raio {spec['radius']:.2f}")
    print('pegada', grip)


if __name__ == '__main__':
    main()
