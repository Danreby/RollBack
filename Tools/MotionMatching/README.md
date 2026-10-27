# Learned Motion Matching da locomoção do player

Pipeline que gera `Assets/MotionMatching/PlayerLocomotion.bytes`, o banco de movimento + as redes
neurais usados por `MotionMatchingLocomotion` (Assets/Scripts/MotionMatching).

Baseado em *Learned Motion Matching* (Holden, Kanoun, Perepichka e Popa — SIGGRAPH 2020,
Ubisoft La Forge) e no código de referência de Daniel Holden (Motion-Matching, theorangeduck.com).

## Como funciona

1. **Banco de movimento** (`build_database.py`): os clipes de parado/andar/correr/andar de lado da
   ExplosiveLLC e da Quaternius (Universal Animation Library, CC0, convertida para o esqueleto do
   RPG-Character por `Tools/AnimationImport`) são lidos direto dos `.FBX`, viram ciclos fechados a
   60 fps e são reamostrados em várias escalas de tempo, cobrindo de ~1 m/s a ~14 m/s:
   - **disparada**: a `Sprint_Loop` da Quaternius — tronco inclinado, passadas longas, braços
     bombeando (a corrida "com toda a vontade"); 10–14 m/s. Com a espada de duas mãos, pernas e
     tronco da disparada e braços segurando a espada (animação em camadas);
   - correr 4,7–9 m/s, andar 0,9–3,6 m/s e andar de lado/para trás (mira travada) até 6,5 m/s;
   - três posturas: desarmado, espada de duas mãos e arma de uma mão (adagas, espadão).

   Para cada frame ficam a pose, as velocidades, o contato dos pés e as *features* do Motion
   Matching:
   - posição e velocidade dos pés, velocidade do quadril (espaço do personagem);
   - trajetória futura: posições e direções em +1/3, +2/3 e +1 s (com a mira travada a direção
     fica no alvo e a velocidade vai para o lado: o banco responde com os passos de lado);
   - postura, velocidade atual da raiz e parado/em movimento.

   Cada grupo é normalizado pelo desvio padrão e multiplicado por um peso (`FEATURE_GROUPS`).
2. **Redes** (`train_lmm.py`, JAX):
   - *Compressor* (só no treino) resume a pose completa em 32 latentes;
   - *Decompressor*: features + latentes → pose (rotações locais em 6 números, quadril,
     velocidade da raiz, contatos);
   - *Stepper*: avança features e latentes um passo de tempo (substitui "ir para o próximo frame");
   - *Projector*: consulta → features/latentes do frame mais parecido (substitui a busca).
3. **Exportação** (`export_unity.py`) grava tudo num arquivo binário lido por
   `MotionDatabase.cs`.

Em jogo (andar 2,4 m/s, correr 7 m/s, disparar 12,5 m/s, andar de lado com a mira travada
4,8 m/s), a cada quadro a simulação (molas criticamente amortecidas no `ThirdPersonController`)
prevê a trajetória do próximo segundo; a cada 0,1 s (ou quando o input muda de repente) o
Projector escolhe o melhor ponto do banco, o Stepper avança e o Decompressor gera a pose. Trocas
de trecho e a passagem para o Animator (golpes, esquiva, pulo) usam inertialização; os pés
travam no chão durante o contato (IK) e, quando precisam mudar de lugar sem a animação levantá-los
(arrancadas, paradas), dão um passo procedural em arco.

## Resultados (medidos com o Harness, mesmos inputs)

| | Antes (blend tree) | Motion Matching (banco) | Learned Motion Matching |
|---|---|---|---|
| Deslize do pé no chão, média (arrancar/parar) | 152 cm/s | 0,6 cm/s | 0,4 cm/s |
| Deslize do pé no chão, média (andar/correr/parar) | 39 cm/s | 1,2 cm/s | 0,3 cm/s |
| Deslize do pé no chão, média (curvas) | 84 cm/s | 1,3 cm/s | 1,0 cm/s |

Redes: Decompressor reconstrói as juntas com erro médio de 0,29 cm (máx. 2,4 cm); o Stepper
sozinho deriva menos de 1 cm em 2 s; a inferência em C# bate com a do treino (diferença < 5e-5).
Custo em .NET 8: ~0,05 ms/frame (banco) e ~0,2–0,4 ms/frame (LMM).

## Regerar

Requisitos: Python 3 com `numpy`, `jax`, `optax` (e `matplotlib` para `analyze.py`).
O treino roda em CPU (~45 min no total).

```bash
cd Tools/MotionMatching
python3 build_database.py          # build/database.npz
python3 train_lmm.py all           # build/lmm_*.npz, build/latent.npy
python3 export_unity.py            # Assets/MotionMatching/PlayerLocomotion.bytes
```

Para usar só o Motion Matching com banco (sem redes): `python3 export_unity.py --no-networks`.

## Testar fora da Unity (opcional, .NET 8)

O núcleo em `Assets/Scripts/MotionMatching/Core` não depende da Unity; o `Harness` roda cenários
de input (andar, correr, parar, curvas, trocar de arma, passar para o Animator e voltar) e grava as
poses:

```bash
python3 evaluate_lmm.py                     # precisão das redes contra o banco
python3 verify_networks.py
dotnet run --project Harness -c Release -- ../../Assets/MotionMatching/PlayerLocomotion.bytes --verify build/verify.bin
dotnet run --project Harness -c Release -- ../../Assets/MotionMatching/PlayerLocomotion.bytes build/harness
python3 analyze.py build/harness/*.bin     # deslize dos pés, estalos, distância da raiz
```

## Adicionar animações

Acrescente o clipe em `CLIPS` (`build_database.py`) com a postura, o tipo
(`idle`/`walk`/`run`/`sprint`/`strafe`) e as escalas de tempo, e rode os três passos de novo. O
clipe precisa ser do esqueleto do RPG-Character (mesmos nomes de ossos); animações de outros
esqueletos humanoides podem ser convertidas como em `Tools/AnimationImport/import_quaternius.py`.
