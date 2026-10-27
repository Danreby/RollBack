"""Gera os conjuntos de golpes das armas (Assets/Weapons/Movesets/*.asset) e os estados do Animator.

Para cada golpe a animação é avaliada no esqueleto do RPG-Character e a ponta da arma (ou o
punho/pé, sem arma) é seguida no espaço da raiz: as janelas de acerto são os trechos em que a ponta
passa de metade da velocidade máxima do golpe. O resto dos tempos (quando dá para emendar o
próximo golpe, quando o movimento cancela, quando o golpe acaba) sai das janelas e do tipo do golpe:
  - golpes de sequência da Quaternius terminam onde o próximo começa e têm uma animação de
    recuperação ("-Rec") tocada quando a sequência para;
  - golpes "inteiros" (ExplosiveLLC, finalizadores) já voltam para a guarda sozinhos.
O deslocamento da raiz (curva do nó Motion) vira AttackData.rootTravel, usado para encurtar o passo
perto do alvo.

Os golpes da espada e dos punhos que já existiam (EEJANAI/ExplosiveLLC) são lidos da cena e
mantidos como sequência leve dessas armas.

Também cria no Animator do player e do boneco os estados das animações novas.

Uso:  python3 movesets.py
"""
import os
import re
import sys
import hashlib
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'MotionMatching'))
import fbx  # noqa: E402

PROJECT = os.path.abspath(os.path.join(HERE, '..', '..'))
PACK = os.path.join(PROJECT, 'Assets', 'ExplosiveLLC', 'RPG Character Mecanim Animation Pack FREE')
MODEL = os.path.join(PACK, 'Models', 'Characters', 'RPG-Character.FBX')
QDIR = os.path.join(PROJECT, 'Assets', 'Animations', 'Quaternius')
X2H = os.path.join(PACK, 'Animations', '2Hand-Sword')
SCENE = os.path.join(PROJECT, 'Assets', 'Scenes', 'prototype.unity')
PLAYER_CONTROLLER = os.path.join(PROJECT, 'Assets', 'Animations', 'PlayerCombo.controller')
DUMMY_CONTROLLER = os.path.join(PROJECT, 'Assets', 'Animations', 'TrainingDummy.controller')
OUT = os.path.join(PROJECT, 'Assets', 'Weapons', 'Movesets')
SCRIPT_GUID = hashlib.md5(b'rollback-script-WeaponMoveset.cs').hexdigest()

# Pegada (mesma de weapons.py): centro do punho e eixo do cabo no espaço da B_R_Hand
FIST = np.array([-0.1723, 0.0178, -0.0069])
AXIS = np.array([-0.0039, -0.0803, 0.9968])
# Distância do punho até a ponta de cada arma (m)
TIP = {'Punhos': None, 'Espada': 1.36, 'EspadaLonga': 2.34, 'Espadao': 2.07, 'Adagas': 0.68}

HIT_BONES = {'B_R_Hand': 18, 'B_L_Hand': 17, 'B_R_Calf': 4, 'B_L_Calf': 3, 'B_R_Foot': 6, 'B_L_Foot': 5}
REACTION = {'Normal': 0, 'Heavy': 1, 'Launch': 2, 'Knockback': 3, 'Slam': 4}


def guid_for(name):
    return hashlib.md5(('rollback-moveset-' + name).encode()).hexdigest()


def q_guid(clip):
    return hashlib.md5(('rollback-quaternius-' + clip).encode()).hexdigest()


def meta_guid(path):
    with open(path + '.meta') as f:
        return re.search(r'guid: ([0-9a-f]{32})', f.read()).group(1)


# ---------------------------------------------------------------- clipes

class Clip:
    """Uma animação: onde está, estado do Animator, referência do asset e análise."""
    _model = None

    def __init__(self, source, name):
        self.source, self.name = source, name
        if source == 'q':
            self.path = os.path.join(QDIR, name + '.fbx')
            self.state = name
            self.ref = f"{{fileID: 7400000, guid: {q_guid(name)}, type: 3}}"
            self.root_motion_xz = True
        else:
            self.path = os.path.join(X2H, f'RPG-Character@2Hand-Sword-{name}.FBX')
            self.state = '2H-' + name
            self.ref = f"{{fileID: 7400000, guid: {meta_guid(self.path)}, type: 3}}"
            self.root_motion_xz = False   # o passo está "assado" na pose (loopBlendPositionXZ)
        self._scene = None

    @property
    def scene(self):
        if self._scene is None:
            self._scene = fbx.Scene(self.path)
        return self._scene

    @classmethod
    def model(cls):
        if cls._model is None:
            cls._model = fbx.Scene(MODEL)
        return cls._model

    @property
    def length(self):
        if self.source == 'q':
            frames = int(re.search(r'lastFrame: (\d+)', open(self.path + '.meta').read()).group(1))
            return frames / 30.0
        return int(re.search(r'lastFrame: (\d+)', open(self.path + '.meta').read()).group(1)) / 30.0

    def sample(self, effector, tip_length, fps=120):
        """Posição da ponta (no espaço da raiz) e da raiz ao longo do clipe."""
        n = max(2, int(self.length * fps) + 1)
        ts = np.linspace(0, self.length, n)
        tips, roots = [], []
        for t in ts:
            W = self.model().world(t, self.scene)
            root = fbx.to_unity(W['Motion'])[:3, 3]
            H = fbx.to_unity(W[effector])
            if tip_length is None:
                p = H[:3, 3]
            else:
                R = H[:3, :3] / np.linalg.norm(H[:3, :3], axis=0)
                p = H[:3, 3] + R @ (FIST + AXIS * tip_length)
            tips.append(p - root)
            roots.append(root)
        return ts, np.array(tips), np.array(roots)


def hit_windows(clip, effector, tip_length):
    ts, tips, roots = clip.sample(effector, tip_length)
    speed = np.linalg.norm(np.gradient(tips, ts, axis=0), axis=1)
    peak = speed.max()
    on = speed >= 0.5 * peak
    windows = []
    i = 0
    while i < len(on):
        if on[i]:
            j = i
            while j + 1 < len(on) and on[j + 1]:
                j += 1
            windows.append([ts[i], ts[j], speed[i:j + 1].max()])
            i = j + 1
        else:
            i += 1
    # junta janelas quase coladas e descarta picos fracos
    merged = []
    for w in windows:
        if merged and w[0] - merged[-1][1] < 0.05 * clip.length:
            merged[-1][1] = w[1]
            merged[-1][2] = max(merged[-1][2], w[2])
        else:
            merged.append(w)
    merged = [w for w in merged if w[2] >= 0.6 * peak]
    out = []
    for a, b, _ in merged:
        a = max(0.0, a / clip.length - 0.02)
        b = min(1.0, b / clip.length + 0.02)
        out.append((round(float(a), 3), round(float(max(b, a + 0.06)), 3)))
    travel = roots[-1] - roots[0]
    return out, travel


def fastest_effector(clip):
    best, name = -1, 'B_R_Hand'
    for eff in ('B_R_Hand', 'B_L_Hand', 'B_R_Calf', 'B_L_Calf', 'B_R_Foot', 'B_L_Foot'):
        ts, tips, _ = clip.sample(eff, None, fps=60)
        s = np.linalg.norm(np.gradient(tips, ts, axis=0), axis=1).max()
        if s > best:
            best, name = s, eff
    return name


# ---------------------------------------------------------------- golpes

def attack(clip_source, clip_name, name, kind, damage, **kw):
    return dict(src=clip_source, clip=clip_name, name=name, kind=kind, damage=damage, **kw)


def Q(clip, name, kind, damage, **kw):
    return attack('q', clip, name, kind, damage, **kw)


def X(clip, name, kind, damage, **kw):
    return attack('x', clip, name, kind, damage, **kw)


def common_air(weapon_speed=1.0):
    return [
        Q('Q-Sword-Aerial-A', 'Corte aéreo', 'air', 8, speed=weapon_speed, launchHeight=0.55),
        Q('Q-Sword-Aerial-B', 'Corte aéreo 2', 'air', 9, speed=weapon_speed, launchHeight=0.6),
        Q('Q-Sword-Aerial-Spin', 'Redemoinho aéreo', 'air', 7, speed=weapon_speed, launchHeight=0.7, heavy=True),
    ]


def launcher(damage=8):
    return Q('Q-Sword-UpperCut', 'Lançador', 'launcher', damage, reaction='Launch', launchHeight=3.4, knockback=0.2)


def slam(damage=14, radius=3.2):
    return Q('Q-Sword-GroundPound', 'Golpe de queda', 'slam', damage, reaction='Slam', knockback=1.2, slamRadius=radius)


MOVESETS = {
    'Punhos': dict(
        displayName='Punhos', stance=0, pose=0.0, attackRange=1.15, idealDistance=1.0, hitArcRange=1.45,
        light='scene:unarmedCombo',
        heavy=[Q('Q-Melee-Hook', 'Gancho pesado', 'ground', 12, heavy=True, knockback=0.7),
               Q('Q-Melee-Knee', 'Joelhada voadora', 'ground', 16, reaction='Knockback', knockback=3.0)],
        launcher=Q('Q-Melee-Uppercut', 'Uppercut', 'ground', 9, reaction='Launch', launchHeight=2.4, knockback=0.2),
        air=common_air(1.1), slam=slam(12, 2.8), dash='scene:sprintAttackUnarmed'),
    'Espada': dict(
        displayName='Espada', stance=1, pose=1.0, attackRange=2.2, idealDistance=1.4, hitArcRange=2.1,
        light='scene:armedCombo',
        heavy=[Q('Q-Sword-Regular-A', 'Estocada', 'ground', 14, rec=True, knockback=0.6),
               Q('Q-Sword-Regular-B', 'Corte rasteiro', 'ground', 15, rec=True, knockback=0.5),
               Q('Q-Sword-Regular-C', 'Corte giratório', 'ground', 24, reaction='Knockback', knockback=3.5)],
        launcher=launcher(10), air=common_air(), slam=slam(16, 3.2), dash='scene:sprintAttackArmed'),
    'EspadaLonga': dict(
        displayName='Espada Longa', stance=1, pose=1.0, attackRange=2.6, idealDistance=1.8, hitArcRange=2.6,
        light=[X('Attack1', 'Corte diagonal', 'ground', 15, speed=1.3, twoHanded=True),
               X('Attack8', 'Corte horizontal', 'ground', 15, speed=1.3, twoHanded=True),
               X('Attack5', 'Corte descendente', 'ground', 17, speed=1.3, twoHanded=True),
               X('Attack4', 'Corte ascendente', 'ground', 22, speed=1.2, twoHanded=True, heavy=True, knockback=1.0)],
        heavy=[X('Attack10', 'Golpe de ponta', 'ground', 20, speed=1.15, twoHanded=True, knockback=0.9),
               X('Attack11', 'Giro alto', 'ground', 20, speed=1.15, twoHanded=True, knockback=0.6),
               X('Attack9', 'Corte do carrasco', 'ground', 30, speed=1.1, twoHanded=True,
                 reaction='Knockback', knockback=4.0)],
        launcher=launcher(12), air=common_air(0.95), slam=slam(18, 3.6), dash='scene:sprintAttackArmed'),
    'Espadao': dict(
        displayName='Espadão', stance=2, pose=0.0, attackRange=2.6, idealDistance=1.9, hitArcRange=2.7,
        light=[Q('Q-Sword-Heavy-A', 'Golpe pesado', 'ground', 20, rec=True, knockback=0.8, heavy=True),
               Q('Q-Sword-Heavy-B', 'Retorno pesado', 'ground', 20, rec=True, knockback=0.8, heavy=True),
               Q('Q-Sword-Heavy-C', 'Estocada pesada', 'ground', 26, rec=True, knockback=1.2, heavy=True)],
        heavy=[Q('Q-Sword-Heavy-D', 'Golpe devastador', 'ground', 38, reaction='Knockback', knockback=4.5)],
        launcher=launcher(14), air=common_air(0.9), slam=slam(26, 4.5),
        dash=Q('Q-Sword-Dash', 'Investida do espadão', 'dash', 22, knockback=1.5, heavy=True)),
    'Adagas': dict(
        displayName='Adagas', stance=2, pose=0.0, attackRange=1.6, idealDistance=1.15, hitArcRange=1.8,
        light=[Q('Q-Sword-Light-A', 'Talho', 'ground', 6, rec=True, speed=1.1, knockback=0.15),
               Q('Q-Sword-Light-B', 'Talho duplo', 'ground', 6, rec=True, speed=1.1, knockback=0.15),
               Q('Q-Sword-Light-C', 'Avanço cruzado', 'ground', 8, rec=True, speed=1.1, knockback=0.25),
               Q('Q-Sword-Light-D', 'Lâminas cruzadas', 'ground', 12, speed=1.1, heavy=True, knockback=1.2)],
        heavy=[Q('Q-Melee-Hook', 'Gancho com adaga', 'ground', 11, heavy=True, knockback=0.7),
               Q('Q-Melee-Knee', 'Joelhada voadora', 'ground', 15, reaction='Knockback', knockback=3.0)],
        launcher=launcher(8), air=common_air(1.2), slam=slam(12, 2.8),
        dash=Q('Q-Sword-Dash', 'Investida', 'dash', 12, knockback=0.8, speed=1.15)),
}

RECOVERY = {'Q-Sword-Light-A', 'Q-Sword-Light-B', 'Q-Sword-Light-C', 'Q-Sword-Regular-A', 'Q-Sword-Regular-B',
            'Q-Sword-Heavy-A', 'Q-Sword-Heavy-B', 'Q-Sword-Heavy-C'}


def human_scale():
    """Escala do avatar humanoide (altura do quadril na T-pose), para converter metros em unidades RootT."""
    W = Clip.model().world()
    return float(fbx.to_unity(W['B_Pelvis'])[1, 3])


def build_attack(spec, weapon, hs, cache):
    clip = cache.setdefault((spec['src'], spec['clip']), Clip(spec['src'], spec['clip']))
    kind = spec['kind']
    unarmed = TIP[weapon] is None
    effector = fastest_effector(clip) if unarmed else 'B_R_Hand'
    windows, travel = hit_windows(clip, effector, TIP[weapon])
    last = max(w[1] for w in windows)
    rec = spec.get('rec', False) and clip.name in RECOVERY

    d = dict(name=spec['name'], stateName=clip.state, clip=clip.ref, speed=spec.get('speed', 1.0),
             damage=spec['damage'], hitWindows=windows, hitBone=HIT_BONES.get(effector, 18),
             recoveryState='', recoveryClip='{fileID: 0}', rootMotionScale=1.0,
             rootTravel=round(float(np.hypot(travel[0], travel[2])) / hs, 3) if clip.root_motion_xz else 0.0,
             carrySpeed=0.0, twoHanded=int(spec.get('twoHanded', False)), knockback=spec.get('knockback', 0.4),
             heavy=int(spec.get('heavy', False)), reaction=REACTION[spec.get('reaction', 'Normal')],
             launchHeight=spec.get('launchHeight', 0.0), transitionDuration=0.1, verticalRootMotion=0,
             airGravityScale=1.0, slamHoldTime=0.0, slamImpactTime=0.0, slamRadius=0.0)
    if rec:
        rclip = cache.setdefault(('q', clip.name + '-Rec'), Clip('q', clip.name + '-Rec'))
        d['recoveryState'] = rclip.state
        d['recoveryClip'] = rclip.ref
        # a sequência emenda logo depois do corte (o golpe seguinte começa na pose em que este termina)
        d['comboTime'] = float(np.clip(last + 0.06, 0.55, 0.92))
        d['recoveryTime'] = max(d['comboTime'], 0.85)
        d['endTime'] = 0.98
    elif kind == 'air':
        d['comboTime'] = float(np.clip(last + 0.05, 0.4, 0.85))
        d['recoveryTime'] = min(d['comboTime'] + 0.05, 0.92)
        d['endTime'] = 0.95
        d['airGravityScale'] = 0.12
        d['transitionDuration'] = 0.08
    elif kind == 'launcher':
        d.update(comboTime=0.72, recoveryTime=0.9, endTime=0.98, verticalRootMotion=1)
    elif kind == 'slam':
        d.update(comboTime=0.5, recoveryTime=0.55, endTime=0.85, airGravityScale=0.5,
                 slamHoldTime=0.18, slamImpactTime=0.29, slamRadius=spec['slamRadius'])
        d['hitWindows'] = [(0.12, 0.32)]
    else:
        d['comboTime'] = float(np.clip(last + 0.12, 0.35, 0.85))
        d['recoveryTime'] = min(d['comboTime'] + 0.05, 0.9)
        d['endTime'] = min(d['recoveryTime'] + 0.2, 0.95)
    for k in ('comboTime', 'recoveryTime', 'endTime'):
        d[k] = round(d[k], 3)
    return d, clip


# ---------------------------------------------------------------- golpes que já estavam na cena

def scene_attacks(field):
    """Lê uma lista (ou um único) AttackData do PlayerCombat da cena."""
    text = open(SCENE).read()
    block = text[text.index('m_Script: {fileID: 11500000, guid: c18553130def08f4abf351cc925d31a0'):]
    block = block[:block.index('\n---')] if '\n---' in block else block
    m = re.search(rf'^  {field}:\n((?:    .*\n|  - .*\n)+)', block, re.M)
    if m is None:
        return None
    lines = m.group(1).splitlines()
    items, cur, key = [], None, None
    for line in lines:
        if line.startswith('  - ') or (cur is None and line.startswith('    ')):
            cur = {}
            items.append(cur)
            line = '    ' + line[4:] if line.startswith('  - ') else line
        content = line[4:]
        if content.startswith('- '):
            cur[key].append(content[2:])
            continue
        k, _, v = content.partition(':')
        key = k.strip()
        v = v.strip()
        cur[key] = [] if v == '' else v
    return items


def from_scene(item, **overrides):
    d = dict(name=item['name'], stateName=item['stateName'], clip=item['clip'], speed=float(item['speed']),
             damage=float(item['damage']),
             hitWindows=[tuple(float(x) for x in re.findall(r'[-\d.]+', w)) for w in item['hitWindows']],
             hitBone=int(item['hitBone']), comboTime=float(item['comboTime']),
             recoveryTime=float(item['recoveryTime']), endTime=float(item['endTime']), recoveryState='',
             recoveryClip='{fileID: 0}', rootMotionScale=float(item['rootMotionScale']),
             rootTravel=float(item['rootTravel']), carrySpeed=float(item['carrySpeed']),
             twoHanded=int(item['twoHanded']), knockback=float(item['knockback']), heavy=int(item['heavy']),
             reaction=REACTION['Heavy'] if int(item['heavy']) else 0, launchHeight=0.0,
             transitionDuration=float(item['transitionDuration']), verticalRootMotion=0, airGravityScale=1.0,
             slamHoldTime=0.0, slamImpactTime=0.0, slamRadius=0.0)
    d.update(overrides)
    return d


# ---------------------------------------------------------------- escrita

FIELDS = ['name', 'stateName', 'clip', 'speed', 'damage', 'hitWindows', 'hitBone', 'comboTime', 'recoveryTime',
          'endTime', 'recoveryState', 'recoveryClip', 'rootMotionScale', 'rootTravel', 'carrySpeed', 'twoHanded',
          'knockback', 'heavy', 'reaction', 'launchHeight', 'transitionDuration', 'verticalRootMotion',
          'airGravityScale', 'slamHoldTime', 'slamImpactTime', 'slamRadius']


def fmt(v):
    if isinstance(v, float):
        return f"{v:.4g}"
    return str(v)


def attack_yaml(d, indent):
    pad = ' ' * indent
    out = []
    for k in FIELDS:
        v = d[k]
        if k == 'hitWindows':
            out.append(f"{pad}{k}:")
            out += [f"{pad}- {{x: {fmt(float(a))}, y: {fmt(float(b))}}}" for a, b in v]
        elif k in ('name', 'stateName', 'recoveryState'):
            out.append(f"{pad}{k}: {v}")
        else:
            out.append(f"{pad}{k}: {fmt(v)}")
    return out


def empty_attack():
    return dict(name='', stateName='', clip='{fileID: 0}', speed=1.0, damage=0.0, hitWindows=[], hitBone=18,
                comboTime=0.6, recoveryTime=0.8, endTime=0.95, recoveryState='', recoveryClip='{fileID: 0}',
                rootMotionScale=1.0, rootTravel=0.0, carrySpeed=0.0, twoHanded=0, knockback=0.4, heavy=0,
                reaction=0, launchHeight=0.0, transitionDuration=0.1, verticalRootMotion=0, airGravityScale=1.0,
                slamHoldTime=0.0, slamImpactTime=0.0, slamRadius=0.0)


def write_moveset(key, spec, attacks):
    lines = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!114 &11400000", "MonoBehaviour:",
             "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
             "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1", "  m_EditorHideFlags: 0",
             f"  m_Script: {{fileID: 11500000, guid: {SCRIPT_GUID}, type: 3}}", f"  m_Name: {key}",
             "  m_EditorClassIdentifier: ", f"  displayName: {spec['displayName']}",
             f"  locomotionStance: {spec['stance']}", f"  animatorPose: {spec['pose']}"]
    for field in ('lightCombo', 'heavyCombo'):
        lines.append(f"  {field}:")
        for d in attacks[field]:
            block = attack_yaml(d, 4)
            block[0] = '  - ' + block[0][4:]
            lines += block
    lines.append("  launcher:")
    lines += attack_yaml(attacks['launcher'] or empty_attack(), 4)
    lines.append("  airCombo:")
    for d in attacks['airCombo']:
        block = attack_yaml(d, 4)
        block[0] = '  - ' + block[0][4:]
        lines += block
    for field in ('slam', 'dashAttack'):
        lines.append(f"  {field}:")
        lines += attack_yaml(attacks[field] or empty_attack(), 4)
    lines += ["  dodgeState: ", "  dodgeClip: {fileID: 0}", f"  attackRange: {spec['attackRange']}",
              f"  idealDistance: {spec['idealDistance']}", f"  hitArcRange: {spec['hitArcRange']}"]
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, key + '.asset')
    with open(path, 'w') as f:
        f.write('\n'.join(lines) + '\n')
    with open(path + '.meta', 'w') as f:
        f.write(f"fileFormatVersion: 2\nguid: {guid_for(key)}\nNativeFormatImporter:\n  externalObjects: {{}}\n"
                "  mainObjectFileID: 11400000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


# ---------------------------------------------------------------- Animator

def file_id(name):
    v = int(hashlib.md5(('rollback-state-' + name).encode()).hexdigest()[:15], 16)
    return v | 1  # ímpar, positivo, longe dos ids que a Unity gera


STATE_TEMPLATE = """--- !u!1102 &{id}
AnimatorState:
  serializedVersion: 6
  m_ObjectHideFlags: 1
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Speed: 1
  m_CycleOffset: 0
  m_Transitions:{transitions}
  m_StateMachineBehaviours: []
  m_Position: {{x: 50, y: 50, z: 0}}
  m_IKOnFeet: 0
  m_WriteDefaultValues: 1
  m_Mirror: 0
  m_SpeedParameterActive: {speed_active}
  m_MirrorParameterActive: 0
  m_CycleOffsetParameterActive: 0
  m_TimeParameterActive: 0
  m_Motion: {motion}
  m_Tag: {tag}
  m_SpeedParameter: {speed_param}
  m_MirrorParameter:
  m_CycleOffsetParameter:
  m_TimeParameter:
"""

TRANSITION_TEMPLATE = """--- !u!1101 &{id}
AnimatorStateTransition:
  m_ObjectHideFlags: 1
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name:
  m_Conditions:{conditions}
  m_DstStateMachine: {{fileID: 0}}
  m_DstState: {{fileID: {dst}}}
  m_Solo: 0
  m_Mute: 0
  m_IsExit: 0
  serializedVersion: 3
  m_TransitionDuration: {duration}
  m_TransitionOffset: 0
  m_ExitTime: {exit_time}
  m_HasExitTime: {has_exit}
  m_HasFixedDuration: 1
  m_InterruptionSource: 0
  m_OrderedInterruption: 1
  m_CanTransitionToSelf: 1
"""


def state_ids(text):
    ids = {}
    for m in re.finditer(r'--- !u!1102 &(-?\d+)\nAnimatorState:\n(?:.*\n)*?  m_Name: (.*)\n', text):
        ids[m.group(2)] = m.group(1)
    return ids


def add_states(path, states, speed_param='ActionSpeed', machine='Base Layer'):
    """states: lista de (nome, motion, [(destino, [(modo, parâmetro)], com tempo de saída)], parâmetro de
    velocidade (None = o padrão, '' = nenhum), tag, tempo de saída)."""
    text = open(path).read()
    ids = state_ids(text)
    new_docs, children = [], []
    y = 700
    for k, (name, motion, dst, own_speed, tag, exit_time) in enumerate(states):
        param = speed_param if own_speed is None else own_speed
        if name in ids:
            # já existe: só atualiza o clipe
            sid = ids[name]
            text = re.sub(rf'(--- !u!1102 &{sid}\nAnimatorState:\n(?:.*\n)*?  m_Motion: ).*\n', rf'\g<1>{motion}\n', text)
            continue
        sid = file_id(path + name)
        trans_lines = ''
        for j, (target, condition, has_exit) in enumerate(dst or []):
            tid = file_id(path + name + '->' + target + str(j))
            trans_lines += f"\n  - {{fileID: {tid}}}"
            cond_text = ' []' if not condition else ''.join(
                f"\n  - m_ConditionMode: {mode}\n    m_ConditionEvent: {param}\n    m_EventTreshold: 0"
                for mode, param in condition)
            new_docs.append(TRANSITION_TEMPLATE.format(
                id=tid, conditions=cond_text, dst=ids[target] if target in ids else file_id(path + target),
                duration=0.2 if has_exit else 0.15, exit_time=exit_time if has_exit else 0,
                has_exit=1 if has_exit else 0))
        new_docs.append(STATE_TEMPLATE.format(
            id=sid, name=name, transitions=trans_lines if trans_lines else ' []',
            speed_active=1 if param else 0, motion=motion, tag=tag, speed_param=param or ''))
        children.append(f"  - serializedVersion: 1\n    m_State: {{fileID: {sid}}}\n"
                        f"    m_Position: {{x: {250 + (k % 4) * 220}, y: {y + (k // 4) * 70}, z: 0}}\n")
        ids[name] = str(sid)
    if children:
        m = re.search(rf'(AnimatorStateMachine:\n(?:.*\n)*?  m_Name: {machine}\n  m_ChildStates:\n(?:  - .*\n(?:    .*\n)*)*)', text)
        text = text[:m.end()] + ''.join(children) + text[m.end():]
        text = text.rstrip('\n') + '\n' + ''.join(new_docs)
    with open(path, 'w') as f:
        f.write(text)
    return len(children)


def main():
    hs = human_scale()
    cache = {}
    report = []
    used = {}   # estado -> (clipe, tipo)
    for key, spec in MOVESETS.items():
        attacks = {}
        for field, src in (('lightCombo', 'light'), ('heavyCombo', 'heavy'), ('airCombo', 'air')):
            value = spec[src]
            if isinstance(value, str):
                attacks[field] = [from_scene(i) for i in scene_attacks(value.split(':')[1])]
            else:
                attacks[field] = []
                for a in value:
                    d, clip = build_attack(a, key, hs, cache)
                    attacks[field].append(d)
                    used[clip.state] = (clip, a['kind'])
        for field, src in (('launcher', 'launcher'), ('slam', 'slam'), ('dashAttack', 'dash')):
            value = spec[src]
            if isinstance(value, str):
                item = scene_attacks(value.split(':')[1])[0]
                # a corrida ficou bem mais rápida: o embalo da investida acompanha
                attacks[field] = from_scene(item, carrySpeed=10.0, knockback=1.0, reaction=REACTION['Heavy'])
            elif value is None:
                attacks[field] = None
            else:
                d, clip = build_attack(value, key, hs, cache)
                attacks[field] = d
                used[clip.state] = (clip, value['kind'])
        for d in attacks['lightCombo'] + attacks['heavyCombo']:
            if d['recoveryState']:
                used[d['recoveryState']] = (cache[('q', d['recoveryState'])], 'rec')
        write_moveset(key, spec, attacks)
        report.append(f"== {spec['displayName']}")
        for field in ('lightCombo', 'heavyCombo', 'launcher', 'airCombo', 'slam', 'dashAttack'):
            items = attacks[field] if isinstance(attacks[field], list) else [attacks[field]]
            for d in items:
                if d:
                    report.append(f"   {field:10s} {d['name']:22s} {d['stateName']:22s} janelas {d['hitWindows']}  "
                                  f"encadeia {d['comboTime']:.2f}  fim {d['endTime']:.2f}  passo {d['rootTravel']:.2f}")

    # Estados novos no Animator do player
    states = []
    for state, (clip, kind) in sorted(used.items()):
        air = kind in ('air',)
        exits = [('JumpFall' if air else 'Locomotion', [], True)]
        states.append((state, clip.ref, exits, None, 'Attack', 1))
    dj = Clip('q', 'Q-DoubleJump')
    states.append(('DoubleJump', dj.ref, [('JumpFall', [], True), ('Locomotion', [(1, 'Grounded')], False)], '',
                   'Jump', 0.7))
    added = add_states(PLAYER_CONTROLLER, states)

    # Reações do boneco
    dummy = [('Launch', 'Q-React-Launch'), ('AirIdle', 'Q-React-Air-Idle'), ('AirHitL', 'Q-React-Air-Hit-L'),
             ('AirHitR', 'Q-React-Air-Hit-R'), ('AirFall', 'Q-React-Air-Fall-Loop'),
             ('FallImpact', 'Q-React-Fall-Impact'), ('KnockbackFly', 'Q-React-Knockback'), ('KipUp', 'Q-React-KipUp')]
    added_dummy = add_states(DUMMY_CONTROLLER, [(s, Clip('q', c).ref, [], None, 'Reaction', 1) for s, c in dummy],
                             speed_param=None)

    # .meta do script do ScriptableObject (guid fixo, referenciado pelos assets)
    script_meta = os.path.join(PROJECT, 'Assets', 'Scripts', 'WeaponMoveset.cs.meta')
    if not os.path.exists(script_meta):
        with open(script_meta, 'w') as f:
            f.write(f"fileFormatVersion: 2\nguid: {SCRIPT_GUID}\nMonoImporter:\n  externalObjects: {{}}\n"
                    "  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n"
                    "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    folder_meta = OUT + '.meta'
    if not os.path.exists(folder_meta):
        with open(folder_meta, 'w') as f:
            f.write(f"fileFormatVersion: 2\nguid: {guid_for('folder')}\nfolderAsset: yes\nDefaultImporter:\n"
                    "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")

    print('\n'.join(report))
    print(f"escala humanoide {hs:.3f}; estados novos: player {added}, boneco {added_dummy}")


if __name__ == '__main__':
    main()
