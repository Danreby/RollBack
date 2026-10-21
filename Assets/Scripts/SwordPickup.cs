using UnityEngine;

// Espada no chão: chegue perto e aperte E para pegar; G larga a espada.
// Ao equipar ela vai para o osso da mão direita com os offsets abaixo.
[RequireComponent(typeof(MeleeWeapon))]
public class SwordPickup : MonoBehaviour
{
    public Vector3 handPositionOffset;
    public Vector3 handRotationOffset;
    [Tooltip("Força com que a espada é jogada ao ser largada")]
    public float dropForce = 2.5f;
    public string promptText = "[E] Pegar espada";

    private PlayerCombat nearbyPlayer;
    private PlayerCombat owner;
    private Rigidbody body;
    private Collider[] colliders;
    private MeleeWeapon weapon;

    public bool IsEquipped => owner != null;

    private void Awake()
    {
        body      = GetComponent<Rigidbody>();
        colliders = GetComponentsInChildren<Collider>(true);
        weapon    = GetComponent<MeleeWeapon>();
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

#if UNITY_EDITOR
        // Reaplica os offsets a cada frame no Editor: ajustar os valores no Inspector
        // durante o Play Mode encaixa a espada em tempo real.
        transform.localPosition    = handPositionOffset;
        transform.localEulerAngles = handRotationOffset;
#endif

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
        transform.localPosition    = handPositionOffset;
        transform.localEulerAngles = handRotationOffset;

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
        SetPhysics(true);
        if (body != null)
        {
            body.linearVelocity  = player.transform.forward * dropForce + Vector3.up * dropForce * 0.6f;
            body.angularVelocity = player.transform.right * 6f;
        }
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
