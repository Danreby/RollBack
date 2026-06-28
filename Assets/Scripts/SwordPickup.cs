using UnityEngine;

public class SwordPickup : MonoBehaviour
{
    public Vector3 handPositionOffset;
    public Vector3 handRotationOffset;

    private static readonly int WeaponBlendHash = Animator.StringToHash("WeaponBlend");

    private bool playerInRange;
    private bool isEquipped;
    private Animator playerAnimator;
    private ThirdPersonController playerController;
    private Rigidbody swordRigidbody;
    private Collider[] swordColliders;

    private void Awake()
    {
        swordRigidbody = GetComponent<Rigidbody>();
        swordColliders = GetComponents<Collider>();
    }

    private void Update()
    {
        if (playerInRange && !isEquipped && Input.GetKeyDown(KeyCode.E))
            Equip();

        // Reaplica os offsets a cada frame enquanto equipada, assim ajustar os
        // valores no Inspector durante o Play Mode encaixa a espada em tempo real.
        if (isEquipped)
        {
            transform.localPosition    = handPositionOffset;
            transform.localEulerAngles = handRotationOffset;
        }
    }

    private void Equip()
    {
        if (playerAnimator == null) return;

        Transform hand = playerAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null) return;

        transform.SetParent(hand);
        transform.localPosition = handPositionOffset;
        transform.localEulerAngles = handRotationOffset;

        // Desliga a física agora que a espada está na mão do personagem
        if (swordRigidbody != null)
        {
            swordRigidbody.isKinematic = true;
            swordRigidbody.detectCollisions = false;
        }
        foreach (Collider col in swordColliders)
            col.enabled = false;

        playerAnimator.SetFloat(WeaponBlendHash, 1f);
        if (playerController != null)
            playerController.SetEquipped(true);

        isEquipped = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        Animator animator = other.GetComponent<Animator>();
        if (animator == null) return;

        playerInRange    = true;
        playerAnimator   = animator;
        playerController = other.GetComponent<ThirdPersonController>();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.GetComponent<Animator>() == playerAnimator)
            playerInRange = false;
    }
}
