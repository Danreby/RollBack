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

    private CharacterController controller;
    private Animator animator;
    private float verticalVelocity;
    private bool wasJumping;

    private static readonly int SpeedHash     = Animator.StringToHash("Speed");
    private static readonly int DirectionHash  = Animator.StringToHash("Direction");
    private static readonly int JumpHash       = Animator.StringToHash("Jump");

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
        return animator.GetCurrentAnimatorStateInfo(0).IsName("Jump");
    }

    private void Update()
    {
        bool isGrounded   = IsGrounded();
        bool isJumping    = IsInJumpState();

        // Landing
        if (isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
            if (wasJumping)
            {
                // Force return to locomotion from Jump state
                if (animator != null)
                {
                    animator.ResetTrigger(JumpHash);
                    animator.CrossFadeInFixedTime("Locomotion", 0.15f);
                }
                wasJumping = false;
            }
        }

        // Jump input
        if (isGrounded && !wasJumping && Input.GetKeyDown(KeyCode.Space))
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            wasJumping       = true;
            if (animator != null)
            {
                animator.ResetTrigger(JumpHash);
                animator.SetTrigger(JumpHash);
            }
        }

        // Locomotion input
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical   = Input.GetAxisRaw("Vertical");
        Vector3 input    = new Vector3(horizontal, 0f, vertical);

        if (input.sqrMagnitude > 0.0001f && !isJumping)
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

            if (animator != null)
            {
                float speed = isSprinting ? 0.8f : 0.3f;
                animator.SetFloat(SpeedHash,     speed, 0.1f, Time.deltaTime);
                animator.SetFloat(DirectionHash, 0f,    0.1f, Time.deltaTime);
            }
        }
        else if (!isJumping)
        {
            if (animator != null)
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
