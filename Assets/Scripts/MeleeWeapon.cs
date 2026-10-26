using UnityEngine;

// Fica na arma (espada). Define o segmento da lâmina usado na detecção de acerto, os pontos
// de pegada do cabo e liga o rastro (trail) durante os golpes. Selecione a espada na Scene
// para ver a lâmina em vermelho e o cabo em amarelo (com o ponto da mão de apoio).
public class MeleeWeapon : MonoBehaviour
{
    [Tooltip("Base da lâmina (perto da guarda), em coordenadas locais da arma")]
    public Vector3 bladeBase = new Vector3(0f, -0.14f, -0.14f);
    [Tooltip("Ponta da lâmina, em coordenadas locais da arma")]
    public Vector3 bladeTip = new Vector3(0f, 0.5f, 0.46f);
    [Tooltip("Raio da área de acerto ao redor da lâmina, em metros")]
    public float bladeRadius = 0.22f;
    public float damageMultiplier = 1f;
    public TrailRenderer trail;

    [Header("Pegada")]
    [Tooltip("Direção do cabo, do pomo para a lâmina, em coordenadas locais da arma")]
    public Vector3 gripAxis = new Vector3(-0.009f, 0.6358f, 0.7718f);
    [Tooltip("Centro do punho da mão principal (direita) no cabo, em coordenadas locais da arma")]
    public Vector3 mainHandGrip = new Vector3(0.0003f, -0.3406f, -0.2809f);
    [Tooltip("Centro do punho da mão de apoio (esquerda) nas animações de duas mãos, em coordenadas locais da arma")]
    public Vector3 offHandGrip = new Vector3(0.0014f, -0.4164f, -0.3729f);

    public Vector3 BaseWorld => transform.TransformPoint(bladeBase);
    public Vector3 TipWorld => transform.TransformPoint(bladeTip);
    public Vector3 GripAxisWorld => transform.TransformDirection(gripAxis).normalized;
    public Vector3 OffHandGripWorld => transform.TransformPoint(offHandGrip);

    private void Awake()
    {
        SetTrail(false);
    }

    public void SetTrail(bool on)
    {
        if (trail == null || trail.emitting == on)
            return;
        trail.emitting = on;
        if (on)
            trail.Clear();
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawLine(BaseWorld, TipWorld);
        Gizmos.DrawWireSphere(BaseWorld, bladeRadius);
        Gizmos.DrawWireSphere(TipWorld, bladeRadius);

        Vector3 main = transform.TransformPoint(mainHandGrip);
        Vector3 off  = OffHandGripWorld;
        Vector3 axis = GripAxisWorld;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(off - axis * 0.08f, main + axis * 0.08f);
        Gizmos.DrawWireSphere(main, 0.03f);
        Gizmos.DrawWireSphere(off, 0.03f);
    }
}
