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

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    private bool IsGrounded()
    {
        Vector3 origin = transform.position + controller.center + Vector3.down * (controller.height / 2f - controller.radius);
        return Physics.SphereCast(origin, controller.radius * 0.9f, Vector3.down, out _, groundCheckDistance, groundMask);
    }

    private void Update()
    {
        bool isGrounded = IsGrounded();

        if (isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
            if (animator != null) animator.ResetTrigger("Jump");
        }

        if (isGrounded && Input.GetKeyDown(KeyCode.Space))
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            if (animator != null) animator.SetTrigger("Jump");
        }

        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical   = Input.GetAxisRaw("Vertical");
        Vector3 input    = new Vector3(horizontal, 0f, vertical);

        if (input.sqrMagnitude > 0.0001f)
        {
            Vector3 camForward = cameraTransform.forward;
            Vector3 camRight   = cameraTransform.right;
            camForward.y = 0f;
            camRight.y   = 0f;
            camForward.Normalize();
            camRight.Normalize();

            Vector3 moveDirection = (camForward * input.z + camRight * input.x).normalized;

            bool isSprinting = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float currentSpeed = isSprinting ? sprintSpeed : moveSpeed;

            controller.Move(moveDirection * currentSpeed * Time.deltaTime);

            Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);

            if (animator != null)
            {
                float speed = isSprinting ? 0.8f : 0.3f;
                animator.SetFloat("Speed",     speed, 0.1f, Time.deltaTime);
                animator.SetFloat("Direction", 0f,    0.1f, Time.deltaTime);
            }
        }
        else
        {
            if (animator != null)
            {
                animator.SetFloat("Speed",     0f, 0.1f, Time.deltaTime);
                animator.SetFloat("Direction", 0f, 0.1f, Time.deltaTime);
            }
        }

        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }
}
