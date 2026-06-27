using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class ThirdPersonController : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float sprintSpeed = 9f;
    public float rotationSpeed = 10f;
    public float gravity = -9.81f;
    public float jumpHeight = 1.5f;
    public float groundCheckDistance = 0.3f;
    public LayerMask groundMask = ~0;
    public Transform cameraTransform;

    public int maxComboSteps = 8;

    // Duração real de cada clipe de golpe (slash1 a slash8 do EEJANAI_Team)
    public float[] attackDurations = { 0.58f, 0.92f, 1.08f, 0.75f, 0.83f, 1.17f, 1.25f, 0.33f };
    public float comboWindowStart = 0.1f;

    // Quanto o personagem avança em cada golpe específico do combo (ajuste livremente)
    public float attack1Distance = 0.3f;
    public float attack2Distance = 0.3f;
    public float attack3Distance = 0.3f;
    public float attack4Distance = 0.3f;
    public float attack5Distance = 0.3f;
    public float attack6Distance = 0.3f;
    public float attack7Distance = 0.3f;
    public float attack8Distance = 0.3f;

    private CharacterController controller;
    private Animator animator;
    private float verticalVelocity;
    private bool wasJumping;

    // Estado do combo controlado inteiramente pelo script (não depende do Animator)
    private bool isAttacking;
    private int comboStep;
    private bool comboQueued;
    private float attackElapsed;

    private static readonly int SpeedHash        = Animator.StringToHash("Speed");
    private static readonly int DirectionHash    = Animator.StringToHash("Direction");
    private static readonly int JumpHash         = Animator.StringToHash("Jump");
    private static readonly int GroundedHash     = Animator.StringToHash("Grounded");
    private static readonly int AttackHash       = Animator.StringToHash("Attack");
    private static readonly int ComboAdvanceHash = Animator.StringToHash("ComboAdvance");

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator   = GetComponent<Animator>();
        if (animator != null) animator.applyRootMotion = false;
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    private bool IsGrounded()
    {
        Vector3 origin = transform.position + controller.center + Vector3.down * (controller.height / 2f - controller.radius);
        return Physics.SphereCast(origin, controller.radius * 0.9f, Vector3.down, out _, groundCheckDistance, groundMask);
    }

    private bool IsInJumpState()
    {
        if (animator == null) return false;
        return animator.GetCurrentAnimatorStateInfo(0).IsTag("Jump");
    }

    private float GetAttackDistance(int step)
    {
        switch (step)
        {
            case 1: return attack1Distance;
            case 2: return attack2Distance;
            case 3: return attack3Distance;
            case 4: return attack4Distance;
            case 5: return attack5Distance;
            case 6: return attack6Distance;
            case 7: return attack7Distance;
            case 8: return attack8Distance;
            default: return 0f;
        }
    }

    private void Update()
    {
        bool isGrounded = IsGrounded();
        bool isJumping  = IsInJumpState();

        if (animator != null)
            animator.SetBool(GroundedHash, isGrounded);

        if (isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
            if (wasJumping)
            {
                if (animator != null)
                    animator.ResetTrigger(JumpHash);
                wasJumping = false;
            }
        }

        if (isGrounded && !wasJumping && !isAttacking && Input.GetKeyDown(KeyCode.Space))
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            wasJumping       = true;
            if (animator != null)
            {
                animator.ResetTrigger(JumpHash);
                animator.SetTrigger(JumpHash);
            }
        }

        UpdateCombat(isJumping);

        bool isMovementBlocked = isAttacking;

        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical   = Input.GetAxisRaw("Vertical");
        Vector3 input    = new Vector3(horizontal, 0f, vertical);

        if (input.sqrMagnitude > 0.0001f && !isMovementBlocked)
        {
            Vector3 camForward = cameraTransform.forward;
            Vector3 camRight   = cameraTransform.right;
            camForward.y = 0f;
            camRight.y   = 0f;
            camForward.Normalize();
            camRight.Normalize();

            Vector3 moveDirection = (camForward * input.z + camRight * input.x).normalized;
            bool isSprinting  = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float currentSpeed = isSprinting ? sprintSpeed : moveSpeed;

            controller.Move(moveDirection * currentSpeed * Time.deltaTime);

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(moveDirection),
                rotationSpeed * Time.deltaTime);

            if (animator != null && !isJumping)
            {
                float speed = isSprinting ? 0.8f : 0.3f;
                animator.SetFloat(SpeedHash,     speed, 0.1f, Time.deltaTime);
                animator.SetFloat(DirectionHash, 0f,    0.1f, Time.deltaTime);
            }
        }
        else if (!isMovementBlocked)
        {
            if (animator != null && !isJumping)
            {
                animator.SetFloat(SpeedHash,     0f, 0.1f, Time.deltaTime);
                animator.SetFloat(DirectionHash, 0f, 0.1f, Time.deltaTime);
            }
        }

        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    private void UpdateCombat(bool isJumping)
    {
        if (Input.GetMouseButtonDown(0) && !isJumping)
        {
            if (!isAttacking)
            {
                isAttacking   = true;
                comboStep     = 1;
                comboQueued   = false;
                attackElapsed = 0f;
                if (animator != null)
                    animator.SetTrigger(AttackHash);
            }
            else
            {
                bool inComboWindow = attackElapsed >= comboWindowStart;
                bool canQueue = inComboWindow && !comboQueued && comboStep < maxComboSteps;
                if (canQueue)
                    comboQueued = true;
            }
        }

        if (!isAttacking)
            return;

        attackElapsed += Time.deltaTime;
        float currentDuration = attackDurations[Mathf.Clamp(comboStep - 1, 0, attackDurations.Length - 1)];

        if (comboQueued && attackElapsed >= currentDuration * 0.85f)
        {
            // O personagem só se move de fato agora, no fim da animação do golpe atual.
            controller.Move(transform.forward * GetAttackDistance(comboStep));

            comboStep++;
            comboQueued   = false;
            attackElapsed = 0f;
            if (animator != null)
                animator.SetTrigger(ComboAdvanceHash);
            return;
        }

        if (attackElapsed >= currentDuration)
        {
            controller.Move(transform.forward * GetAttackDistance(comboStep));

            isAttacking = false;
            comboStep   = 0;
            comboQueued = false;
        }
    }

    // Usado pela câmera para "percorrer" visualmente a distância do golpe atual
    // suavemente durante a animação, mesmo que o personagem só se mova de fato no final.
    public Vector3 GetCombatCameraOffset()
    {
        if (!isAttacking) return Vector3.zero;

        float duration = attackDurations[Mathf.Clamp(comboStep - 1, 0, attackDurations.Length - 1)];
        float t = Mathf.Clamp01(attackElapsed / duration);
        return transform.forward * (GetAttackDistance(comboStep) * t);
    }
}
