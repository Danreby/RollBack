using UnityEngine;

// Conjunto de golpes de uma arma (ou dos punhos), no estilo hack'n slash:
//   - sequência leve (clique esquerdo) e pesada (clique direito). Do meio de uma sequência dá para
//     trocar para a outra: o próximo golpe continua do mesmo ponto na outra sequência;
//   - lançador (Q): joga o alvo para o alto e sobe junto;
//   - sequência aérea (clique no ar) e golpe de queda (clique direito no ar);
//   - investida (clique disparando).
// Os assets ficam em Assets/Weapons/Movesets e são gerados por Tools/AnimationImport/movesets.py
// (janelas de acerto medidas pela velocidade da ponta da arma em cada animação).
[CreateAssetMenu(menuName = "RollBack/Weapon Moveset", fileName = "Moveset")]
public class WeaponMoveset : ScriptableObject
{
    public string displayName = "Arma";
    [Tooltip("Postura da locomoção no Motion Matching: 0 = desarmado, 1 = espada de duas mãos, 2 = arma de uma mão")]
    public int locomotionStance;
    [Tooltip("Pose do Animator (pulo, queda): 0 = desarmado, 1 = espada de duas mãos")]
    [Range(0f, 1f)] public float animatorPose;

    [Header("Golpes")]
    public AttackData[] lightCombo;
    public AttackData[] heavyCombo;
    public AttackData launcher;
    public AttackData[] airCombo;
    public AttackData slam;
    public AttackData dashAttack;

    [Header("Esquiva (vazio = a padrão do PlayerCombat)")]
    public string dodgeState;
    public AnimationClip dodgeClip;

    [Header("Distâncias")]
    [Tooltip("Mais longe que isso (centro a centro), o personagem avança até essa distância antes do golpe sair")]
    public float attackRange = 2.2f;
    [Tooltip("Distância em que o golpe deve terminar: o passo da animação é encurtado para chegar nela")]
    public float idealDistance = 1.4f;
    [Tooltip("Alcance da área de acerto generosa à frente durante a janela do golpe")]
    public float hitArcRange = 2.1f;
}
