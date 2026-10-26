using UnityEngine;

// Espada no chão: chegue perto e aperte E para pegar; G larga a espada.
// Ao equipar ela vai para o osso da mão direita com os offsets abaixo. Os valores padrão
// foram calculados a partir da geometria: o cabo passa exatamente pelo punho fechado
// (mesma linha de pegada da espada original do pacote da ExplosiveLLC) e a guarda fica
// encostada logo acima do indicador.
[RequireComponent(typeof(MeleeWeapon))]
public class SwordPickup : MonoBehaviour
{
    public Vector3 handPositionOffset = new Vector3(-0.0721f, -0.0289f, 0.5168f);
    public Vector3 handRotationOffset = new Vector3(3.9f, 320.19f, 266.67f);
    [Tooltip("Tempo para a espada encaixar na mão ao ser pega (0 = instantâneo)")]
    public float equipBlendTime = 0.12f;
    [Tooltip("Força com que a espada é jogada ao ser largada")]
    public float dropForce = 2.5f;
    public string promptText = "[E] Pegar espada";

    private PlayerCombat nearbyPlayer;
    private PlayerCombat owner;
    private Rigidbody body;
    private Collider[] colliders;
    private MeleeWeapon weapon;
    private Vector3 worldScale;

    // Transição até a mão: pose (relativa à mão) de onde a espada saiu e progresso 0..1
    private Vector3 equipFromPosition;
    private Quaternion equipFromRotation;
    private float equipProgress = 1f;

    public bool IsEquipped => owner != null;

    private void Awake()
    {
        body       = GetComponent<Rigidbody>();
        colliders  = GetComponentsInChildren<Collider>(true);
        weapon     = GetComponent<MeleeWeapon>();
        worldScale = transform.lossyScale;
    }

    private void Update()
    {
        if (owner == null)
        {
            bool canPick = nearbyPlayer != null && nearbyPlayer.isActiveAndEnabled
                           && !nearbyPlayer.IsArmed && !nearbyPlayer.IsBusy;
            CombatHUD.SetPrompt(this, canPick ? promptText : null);

            if (canPick && nearbyPlayer.Input != null && nearbyPlayer.Input.InteractPressed)
                Equip(nearbyPlayer);
            return;
        }

        if (equipProgress < 1f)
            equipProgress = Mathf.MoveTowards(equipProgress, 1f, Time.deltaTime / Mathf.Max(0.01f, equipBlendTime));
        // Reaplicado todo frame: ajustar os offsets no Inspector durante o Play Mode encaixa a espada em tempo real.
        ApplyGrip();

        if (owner.Input != null && owner.Input.DropPressed && !owner.IsBusy)
            Drop();
    }

    public bool Equip(PlayerCombat player)
    {
        if (player == null || owner != null || player.IsArmed)
            return false;

        Animator animator = player.GetComponent<Animator>();
        Transform hand    = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (hand == null)
            return false;

        // Desliga a física antes de prender na mão
        SetPhysics(false);
        transform.SetParent(hand, true);
        transform.localScale = DivideScale(worldScale, hand.lossyScale);

        // Parte da pose atual (já relativa à mão) e encaixa em equipBlendTime
        equipFromPosition = transform.localPosition;
        equipFromRotation = transform.localRotation;
        equipProgress     = equipBlendTime > 0f ? 0f : 1f;
        ApplyGrip();

        owner        = player;
        nearbyPlayer = null;
        CombatHUD.SetPrompt(this, null);
        player.EquipWeapon(weapon);
        return true;
    }

    public void Drop()
    {
        if (owner == null)
            return;

        PlayerCombat player = owner;
        player.UnequipWeapon();
        owner = null;

        transform.SetParent(null, true);
        transform.localScale = worldScale;
        SetPhysics(true);
        if (body != null)
        {
            body.linearVelocity  = player.transform.forward * dropForce + Vector3.up * dropForce * 0.6f;
            body.angularVelocity = player.transform.right * 6f;
        }
    }

    private void ApplyGrip()
    {
        Vector3 position    = handPositionOffset;
        Quaternion rotation = Quaternion.Euler(handRotationOffset);
        if (equipProgress < 1f)
        {
            // Desacelera no fim: a espada "assenta" na mão em vez de parar seco
            float t  = 1f - Mathf.Pow(1f - equipProgress, 3f);
            position = Vector3.Lerp(equipFromPosition, position, t);
            rotation = Quaternion.Slerp(equipFromRotation, rotation, t);
        }
        transform.SetLocalPositionAndRotation(position, rotation);
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
        if (player != null && player == nearbyPlayer)
        {
            nearbyPlayer = null;
            CombatHUD.SetPrompt(this, null);
        }
    }

    private void TrackPlayer(Collider other)
    {
        // Só o player pega a espada (antes qualquer Animator, inclusive um inimigo, contava).
        if (owner != null)
            return;
        PlayerCombat player = other.GetComponentInParent<PlayerCombat>();
        if (player != null)
            nearbyPlayer = player;
    }

    private void OnDisable()
    {
        CombatHUD.SetPrompt(this, null);
    }
}
