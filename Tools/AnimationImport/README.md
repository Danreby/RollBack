# Animações e armas novas (Quaternius, CC0)

Ferramentas que trazem para o projeto as animações e armas gratuitas usadas pela disparada, pelo
combate hack'n slash e pelas reações dos bonecos de treino.

## Fontes e licenças

| Asset | Origem | Licença |
|---|---|---|
| Universal Animation Library 1 e 2 (`UAL1_Standard.glb`, `UAL2_Source_RM.glb`): disparada, andar/correr com arma, sequências de espada leve/normal/pesada, lançador, golpe de queda, combo aéreo, investida, socos, pulo duplo e reações (lançado, golpes no ar, queda, arremesso, kip-up) | Quaternius ([quaternius.com](https://quaternius.com)), cópia pública em [Arod231/monomachia](https://github.com/Arod231/monomachia) | CC0 1.0 |
| LowPoly Medieval Weapons (`Dagger.fbx`, `Sword_Big.fbx`): adagas e espadão | Quaternius ([quaternius.itch.io](https://quaternius.itch.io)), mesma cópia pública | CC0 1.0 |
| Espada longa (`2Hand-Sword.FBX`) e golpes de duas mãos (`2Hand-Sword-Attack*.FBX`) | RPG Character Mecanim Animation Pack FREE (ExplosiveLLC), já no projeto | licença do pacote |

Os arquivos baixados ficam em `source/` (fora do git); os scripts baixam o que faltar.

## Scripts

```bash
cd Tools/AnimationImport
python3 import_quaternius.py   # animações -> Assets/Animations/Quaternius/*.fbx (+ .meta)
python3 weapons.py             # malhas e materiais das armas -> Assets/Weapons/Meshes, Materials
python3 movesets.py            # golpes -> Assets/Weapons/Movesets/*.asset + estados do Animator
```

- **import_quaternius.py** — retarget da Quaternius para o esqueleto do RPG-Character: para cada
  osso, a rotação global relativa à T-pose da Quaternius é aplicada sobre a T-pose do RPG-Character;
  o deslocamento da raiz e a altura do quadril são escalados pela razão das alturas do quadril
  (1,451). O FBX gerado clona um clipe da ExplosiveLLC (mesmos nós e unidades) e o `.meta` usa o
  avatar do RPG-Character, então a Unity importa igual aos clipes que já estavam no projeto.
  Nos golpes que sobem ou descem (lançador, golpe de queda, reações no ar) a altura vai para o nó
  Motion: quem controla a altura em jogo é o personagem (gravidade, lançamento), não a pose.
- **weapons.py** — converte as armas para um `.bytes` simples lido por `WeaponMesh.cs`, no "espaço
  da pegada": origem no centro do punho fechado, +Y do pomo para a ponta e X ao longo do fio. O fio
  fica para onde os nós dos dedos apontam, que é para onde a ponta corre nos golpes da Quaternius
  (medido nas animações). Assim todas as armas usam o mesmo encaixe na mão (o da espada da
  ExplosiveLLC, cujo punho fechado o Animator aplica na mão direita).
- **movesets.py** — para cada golpe avalia a animação no esqueleto e segue a ponta da arma: as
  janelas de acerto são os trechos em que a ponta passa de metade da velocidade máxima; os tempos
  de encadear/cancelar/terminar saem das janelas e do tipo do golpe; o deslocamento da raiz vira o
  passo que é encurtado perto do alvo. Também cria os estados das animações novas nos Animators do
  player e do boneco.

## Armas

| Arma (tecla) | Sequência leve (clique) | Pesada (botão direito) | Locomoção |
|---|---|---|---|
| Punhos (1) | 4 socos | gancho, joelhada voadora (arremessa) | guarda de lutador |
| Espada (2) | 7 cortes (EEJANAI) | estocada, corte rasteiro, corte giratório | espada de duas mãos |
| Espada Longa (3) | 4 cortes de duas mãos (ExplosiveLLC) | golpe de ponta, giro alto, corte do carrasco | espada de duas mãos |
| Espadão (4) | 3 golpes pesados | golpe devastador | arma de uma mão |
| Adagas (5) | 4 talhos com as duas lâminas | gancho, joelhada | arma de uma mão |

Todas têm lançador (Q), combo aéreo (clique no ar), golpe de queda (botão direito no ar) e
investida (clique disparando).
