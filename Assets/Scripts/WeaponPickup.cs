using UnityEngine;

// Arma no chão: chegue perto e aperte F para pegar (o PlayerCombat escolhe a mais próxima).
// As armas pegas ficam no arsenal (teclas 1 a 5); G larga a arma da mão.
//
// Ao equipar a arma vai para o osso da mão direita com os offsets abaixo; a segunda lâmina
// (adagas duplas, MeleeWeapon.offhand) vai para a mão esquerda com o encaixe espelhado. As armas
// novas usam malhas no "espaço da pegada" (WeaponMesh), então todas compartilham o mesmo encaixe
// (o da espada da ExplosiveLLC, cujo punho fechado o Animator aplica na mão direita).
// Até ser pega pela primeira vez a arma fica flutuando e girando no lugar, como num pedestal.
[RequireComponent(typeof(MeleeWeapon))]
public class WeaponPickup : MonoBehaviour
{
    public Vector3 handPositionOffset = new Vector3(-0.1723f, 0.0178f, -0.0069f);
    public Vector3 handRotationOffset = new Vector3(85.394f, 180f, 180.223f);
    [Tooltip("Encaixe da segunda lâmina na mão esquerda (espelho do encaixe da direita)")]
    public Vector3 offhandPositionOffset = new Vector3(-0.1723f, 0.0178f, 0.0069f);
    public Vector3 offhandRotationOffset = new Vector3(274.606f, 180f, 180.223f);
    [Tooltip("Tempo para a arma encaixar na mão ao ser pega (0 = instantâneo)")]
    public float equipBlendTime = 0.12f;
    [Tooltip("Força com que a arma é jogada ao ser largada")]
    public float dropForce = 2.5f;

    [Header("Exposição")]
    [Tooltip("Flutua e gira no lugar até ser pega pela primeira vez")]
    public bool hoverUntilPicked = true;
    public float hoverAmplitude = 0.12f;
    public float hoverSpinSpeed = 60f;

    private PlayerCombat owner;
    private Rigidbody body;
    private Collider[] colliders;
    private Renderer[] renderers;
    private Renderer[] offhandRenderers;
    private MeleeWeapon weapon;
    private Vector3 worldScale;
    private Vector3 offhandWorldScale;
    private Vector3 offhandRestPosition;
    private Quaternion offhandRestRotation;
    private bool hovering;
    private Vector3 hoverOrigin;
    private Quaternion hoverRotation;
    private float hoverPhase;

    // Transição até a mão: pose (relativa à mão) de onde a arma saiu e progresso 0..1
    private Vector3 equipFromPosition, offhandFromPosition;
    private Quaternion equipFromRotation, offhandFromRotation;
    private float equipProgress = 1f;

    public bool IsEquipped => owner != null;
    public MeleeWeapon Weapon => weapon;

    private void Awake()
    {
        body       = GetComponent<Rigidbody>();
        colliders  = GetComponentsInChildren<Collider>(true);
        weapon     = GetComponent<MeleeWeapon>();
        worldScale = transform.lossyScale;
        renderers  = GetComponentsInChildren<Renderer>(true);
        if (weapon.offhand != null && weapon.offhand != weapon)
        {
            Transform off = weapon.offhand.transform;
            offhandWorldScale   = off.lossyScale;
            offhandRestPosition = transform.InverseTransformPoint(off.position);
            offhandRestRotation = Quaternion.Inverse(transform.rotation) * off.rotation;
            offhandRenderers    = off.GetComponentsInChildren<Renderer>(true);
        }

        hovering = hoverUntilPicked;
        if (hovering)
        {
            hoverOrigin   = transform.position;
            hoverRotation = transform.rotation;
            hoverPhase    = Random.value * 10f;
            if (body != null)
                body.isKinematic = true;
            // No pedestal só a área de pegar fica ativa (a arma flutuando não é um obstáculo)
            foreach (Collider col in colliders)
                if (!col.isTrigger)
                    col.enabled = false;
        }
    }

    private void Update()
    {
        if (owner == null)
        {
            if (hovering)
            {
                // Pedestal: sobe e desce devagar girando em torno do eixo vertical
                float t = Time.time + hoverPhase;
                transform.SetPositionAndRotation(hoverOrigin + Vector3.up * (Mathf.Sin(t * 2f) * hoverAmplitude),
                                                 Quaternion.AngleAxis(t * hoverSpinSpeed, Vector3.up) * hoverRotation);
            }
            return;
        }

        if (equipProgress < 1f)
            equipProgress = Mathf.MoveTowards(equipProgress, 1f, Time.deltaTime / Mathf.Max(0.01f, equipBlendTime));
        // Reaplicado todo frame: ajustar os offsets no Inspector durante o Play Mode encaixa a arma em tempo real.
        ApplyGrip();
    }

    public bool Equip(PlayerCombat player)
    {
        if (player == null || owner != null)
            return false;

        Animator animator = player.GetComponent<Animator>();
        Transform hand    = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
        Transform leftHand = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
        if (hand == null)
            return false;

        hovering = false;
        // Desliga a física antes de prender na mão
        SetPhysics(false);
        transform.SetParent(hand, true);
        transform.localScale = DivideScale(worldScale, hand.lossyScale);
        equipFromPosition = transform.localPosition;
        equipFromRotation = transform.localRotation;

        Transform off = Offhand;
        if (off != null && leftHand != null)
        {
            off.SetParent(leftHand, true);
            off.localScale      = DivideScale(offhandWorldScale, leftHand.lossyScale);
            offhandFromPosition = off.localPosition;
            offhandFromRotation = off.localRotation;
        }

        // Parte da pose atual (já relativa à mão) e encaixa em equipBlendTime
        equipProgress = equipBlendTime > 0f ? 0f : 1f;
        ApplyGrip();

        owner = player;
        player.UnregisterPickup(this);
        player.AddWeapon(weapon);
        return true;
    }

    // Chamado pelo PlayerCombat ao largar a arma (G, ou trocar por outra do mesmo atalho)
    public void Release(PlayerCombat player)
    {
        if (owner == null)
            return;
        owner = null;
        SetHolstered(false);

        transform.SetParent(null, true);
        transform.localScale = worldScale;
        Transform off = Offhand;
        if (off != null)
        {
            // A segunda lâmina cai junto, presa ao lado da primeira
            off.SetParent(transform, false);
            off.localScale = DivideScale(offhandWorldScale, transform.lossyScale);
            off.SetLocalPositionAndRotation(offhandRestPosition, offhandRestRotation);
        }
        SetPhysics(true);
        if (body != null && player != null)
        {
            body.linearVelocity  = player.transform.forward * dropForce + Vector3.up * dropForce * 0.6f;
            body.angularVelocity = player.transform.right * 6f;
        }
    }

    // Arma guardada (outra está na mão): some de vista, mas continua presa à mão
    public void SetHolstered(bool holstered)
    {
        foreach (Renderer r in renderers)
            if (r != null && !(r is TrailRenderer))
                r.enabled = !holstered;
        if (offhandRenderers != null)
            foreach (Renderer r in offhandRenderers)
                if (r != null && !(r is TrailRenderer))
                    r.enabled = !holstered;
    }

    private Transform Offhand => weapon.offhand != null && weapon.offhand != weapon ? weapon.offhand.transform : null;

    private void ApplyGrip()
    {
        float t = equipProgress < 1f ? 1f - Mathf.Pow(1f - equipProgress, 3f) : 1f; // desacelera no fim
        Quaternion rotation = Quaternion.Euler(handRotationOffset);
        transform.SetLocalPositionAndRotation(Vector3.Lerp(equipFromPosition, handPositionOffset, t),
                                              Quaternion.Slerp(equipFromRotation, rotation, t));
        Transform off = Offhand;
        if (off != null && off.parent != transform)
            off.SetLocalPositionAndRotation(Vector3.Lerp(offhandFromPosition, offhandPositionOffset, t),
                                            Quaternion.Slerp(offhandFromRotation, Quaternion.Euler(offhandRotationOffset), t));
    }

    private static Vector3 DivideScale(Vector3 world, Vector3 parent)
    {
        return new Vector3(world.x / Mathf.Max(1e-4f, Mathf.Abs(parent.x)),
                           world.y / Mathf.Max(1e-4f, Mathf.Abs(parent.y)),
                           world.z / Mathf.Max(1e-4f, Mathf.Abs(parent.z)));
    }

    private void SetPhysics(bool enabled)
    {
        if (body != null)
        {
            body.isKinematic      = !enabled;
            body.detectCollisions = enabled;
            body.interpolation    = enabled ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
        }
        foreach (Collider col in colliders)
            col.enabled = enabled;
    }

    private void OnTriggerEnter(Collider other)
    {
        TrackPlayer(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TrackPlayer(other);
    }

    private void OnTriggerExit(Collider other)
    {
        PlayerCombat player = other.GetComponentInParent<PlayerCombat>();
        if (player != null)
            player.UnregisterPickup(this);
    }

    private void TrackPlayer(Collider other)
    {
        // Só o player pega armas (antes qualquer Animator, inclusive um inimigo, contava).
        if (owner != null)
            return;
        PlayerCombat player = other.GetComponentInParent<PlayerCombat>();
        if (player != null)
            player.RegisterPickup(this);
    }

    private void OnDisable()
    {
        PlayerCombat player = FindFirstObjectByType<PlayerCombat>();
        if (player != null)
            player.UnregisterPickup(this);
    }
}
