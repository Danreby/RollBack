using UnityEngine;

// Fica na arma (espada). Define o segmento da lâmina usado na detecção de acerto
// e liga o rastro (trail) durante os golpes. Selecione a espada na Scene para ver
// a lâmina desenhada em vermelho.
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

    public Vector3 BaseWorld => transform.TransformPoint(bladeBase);
    public Vector3 TipWorld => transform.TransformPoint(bladeTip);

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
    }
}
