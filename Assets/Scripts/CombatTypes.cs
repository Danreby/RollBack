using UnityEngine;

// Tipos compartilhados pelo sistema de combate.

// Tudo que pode levar dano (player, bonecos de treino, futuros inimigos).
public interface IDamageable
{
    bool CanBeHit { get; }
    bool TakeDamage(DamageInfo info);
}

public struct DamageInfo
{
    public float amount;
    public Vector3 point;      // ponto de contato, em coordenadas de mundo
    public Vector3 direction;  // direção horizontal do golpe (do atacante para o alvo)
    public float knockback;    // distância do empurrão, em metros
    public bool heavy;         // golpe forte (finalizador): reação e feedback maiores
    public GameObject source;
}

// Um golpe do combo. Os tempos são normalizados (0 = início do clipe, 1 = fim),
// então continuam corretos mesmo se a velocidade do golpe mudar.
[System.Serializable]
public class AttackData
{
    public string name = "Golpe";

    [Tooltip("Nome do estado no Animator Controller")]
    public string stateName;
    [Tooltip("Clipe do estado (usado para saber a duração)")]
    public AnimationClip clip;
    [Tooltip("Multiplicador de velocidade da animação")]
    public float speed = 1f;

    public float damage = 10f;
    [Tooltip("Janelas de acerto (x = início, y = fim). Cada janela pode acertar o mesmo alvo uma vez")]
    public Vector2[] hitWindows = { new Vector2(0.3f, 0.5f) };
    [Tooltip("Sem arma: osso que acerta (mão/pé)")]
    public HumanBodyBones hitBone = HumanBodyBones.RightHand;

    [Tooltip("A partir daqui o próximo golpe pode começar, se houver clique no buffer")]
    [Range(0f, 1f)] public float comboTime = 0.6f;
    [Tooltip("A partir daqui andar, pular ou esquivar cancela o golpe")]
    [Range(0f, 1f)] public float recoveryTime = 0.8f;
    [Tooltip("Aqui o golpe termina e o personagem volta para a locomoção")]
    [Range(0f, 1f)] public float endTime = 0.95f;

    [Tooltip("Escala do deslocamento da animação (root motion). 0 = golpe parado")]
    public float rootMotionScale = 1f;
    [Tooltip("Empurrão aplicado no alvo, em metros")]
    public float knockback = 0.4f;
    [Tooltip("Finalizador: mais hit stop, tremor de câmera e reação forte no alvo")]
    public bool heavy;
    public float transitionDuration = 0.1f;

    public float Duration => clip != null ? clip.length / Mathf.Max(0.05f, speed) : 0.8f;

    public float LastHitWindowEnd
    {
        get
        {
            float end = 0f;
            if (hitWindows != null)
                foreach (Vector2 w in hitWindows) end = Mathf.Max(end, w.y);
            return end;
        }
    }
}
