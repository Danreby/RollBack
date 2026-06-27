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

    public int maxComboSteps = 3;
    public float comboWindowStart = 0.1f;
    public float comboWindowEnd = 1.5f;

    private CharacterController controller;
    private Animator animator;
    private float verticalVelocity;
    private bool wasJumping;
    private int comboStep;

    private static readonly int SpeedHash      = Animator.StringToHash("Speed");
    private static readonly int DirectionHash  = Animator.StringToHash("Direction");
    private static readonly int JumpHash       = Animator.StringToHash("Jump");
    private static readonly int GroundedHash   = Animator.StringToHash("Grounded");
    private static readonly int AttackHash     = Animator.StringToHash("Attack");
    private static readonly int ComboStepHash  = Animator.StringToHash("ComboStep");

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

    private bool IsInAttackState()
    {
        if (animator == null) return false;
        return animator.GetCurrentAnimatorStateInfo(0).IsTag("Attack");
    }

    private void Update()
    {
        bool isGrounded = IsGrounded();
        bool isJumping  = IsInJumpState();
        bool isAttacking = IsInAttackState();

        if (animator != null)
            animator.SetBool(GroundedHash, isGrounded);

        // Landing
        if (isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
            if (wasJumping)
            {
                // A transição de volta a Locomotion é feita pelo próprio Animator
                // (JumpFall -> JumpLand -> Locomotion) usando o parâmetro Grounded
                if (animator != null)
                    animator.ResetTrigger(JumpHash);
                wasJumping = false;
            }
        }

        // Jump input
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

        // Attack input / combo
        if (Input.GetMouseButtonDown(0) && isGrounded && !isJumping)
        {
            if (!isAttacking)
            {
                comboStep = 1;
                if (animator != null)
                {
                    animator.SetInteger(ComboStepHash, comboStep);
                    animator.SetTrigger(AttackHash);
                }
            }
            else
            {
                // Janela de combo em segundos reais, independente da duração/velocidade do clipe
                AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                float elapsedSeconds = stateInfo.length * stateInfo.normalizedTime;
                bool inComboWindow = elapsedSeconds >= comboWindowStart && elapsedSeconds <= comboWindowEnd;

                if (inComboWindow && comboStep < maxComboSteps)
                {
                    comboStep++;
                    animator.SetInteger(ComboStepHash, comboStep);
                    animator.SetTrigger(AttackHash);
                }
            }
        }

        if (!isAttacking)
            comboStep = 0;

        // Atacar bloqueia movimento, mas pular não deve travar o controle horizontal no ar
        bool isMovementBlocked = isAttacking;

        // Locomotion input
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

        // Gravity + vertical movement
        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }
}
