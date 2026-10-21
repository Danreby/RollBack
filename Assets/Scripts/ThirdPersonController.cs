using UnityEngine;

// Locomoção em terceira pessoa: anda/corre relativo à câmera, pula e aplica gravidade.
// Durante ações do PlayerCombat (golpes e esquiva) o input de movimento é ignorado e o
// deslocamento vem da própria animação (root motion), aplicado em OnAnimatorMove.
[RequireComponent(typeof(CharacterController))]
public class ThirdPersonController : MonoBehaviour
{
    [Header("Movimento")]
    public float moveSpeed = 5f;
    public float sprintSpeed = 9f;
    [Tooltip("Quão rápido a velocidade alcança o alvo no chão (maior = mais responsivo)")]
    public float groundAcceleration = 12f;
    [Tooltip("Mesma ideia, no ar (menor = menos controle durante o pulo)")]
    public float airAcceleration = 3f;
    public float rotationSpeed = 12f;

    [Header("Pulo e gravidade")]
    public float gravity = -22f;
    public float jumpHeight = 1.6f;
    [Tooltip("Tempo depois de sair de uma borda em que ainda dá para pular")]
    public float coyoteTime = 0.12f;
    [Tooltip("Apertar pulo um pouco antes de tocar o chão ainda conta")]
    public float jumpBufferTime = 0.15f;
    public float groundCheckDistance = 0.2f;
    public LayerMask groundMask = ~0;

    [Header("Referências")]
    public Transform cameraTransform;
    public PlayerInputReader input;

    private CharacterController controller;
    private Animator animator;
    private Vector3 planarVelocity;
    private float verticalVelocity;
    private bool isGrounded;
    private bool jumping;
    private float lastGroundedTime    = -999f;
    private float lastJumpPressedTime = -999f;
    private float ignoreGroundUntil;

    // Estado controlado pelo PlayerCombat
    private bool actionLocked;
    private float rootMotionScale;
    private bool hasFacingOverride;
    private Vector3 facingOverride;
    private float facingOverrideSpeed;

    private static readonly int SpeedHash    = Animator.StringToHash("Speed");
    private static readonly int JumpHash     = Animator.StringToHash("Jump");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");

    public Animator Animator => animator;
    public CharacterController Controller => controller;
    public bool IsGrounded => isGrounded;
    public bool IsActionLocked => actionLocked;
    // Direção desejada relativa à câmera (magnitude 0..1)
    public Vector3 MoveDirection { get; private set; }
    public bool HasMoveInput => MoveDirection.sqrMagnitude > 0.01f;
    public bool IsSprinting { get; private set; }

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator   = GetComponent<Animator>();
        if (input == null)
            input = GetComponent<PlayerInputReader>();
        if (animator != null)
        {
            // Com OnAnimatorMove implementado a Unity não move o personagem sozinha:
            // o script decide quando o root motion vale (só durante golpes e esquiva).
            animator.applyRootMotion = true;
            // A detecção de acerto lê os ossos (mão/espada): eles não podem congelar por culling.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        UpdateGrounded();
        ReadMoveInput();
        UpdateJump();
        UpdatePlanarVelocity(dt);
        UpdateRotation(dt);
        UpdateGravity(dt);

        controller.Move((planarVelocity + Vector3.up * verticalVelocity) * dt);
        UpdateAnimator(dt);
    }

    private void UpdateGrounded()
    {
        bool wasGrounded = isGrounded;
        isGrounded = Time.time >= ignoreGroundUntil
                     && verticalVelocity <= 0.01f
                     && (controller.isGrounded || CheckGround());

        if (!isGrounded)
            return;

        lastGroundedTime = Time.time;
        if (!wasGrounded && jumping)
        {
            jumping = false;
            if (animator != null)
                animator.ResetTrigger(JumpHash);
        }
    }

    private bool CheckGround()
    {
        // Esfera um pouco menor que a cápsula, lançada para baixo a partir da base dela.
        // Triggers (ex.: área de pegar a espada) não contam como chão.
        float radius        = controller.radius * 0.9f;
        Vector3 center      = transform.TransformPoint(controller.center);
        float halfHeight    = Mathf.Max(controller.height * 0.5f, controller.radius);
        Vector3 origin      = center + Vector3.down * (halfHeight - controller.radius);
        float castDistance  = (controller.radius - radius) + controller.skinWidth + groundCheckDistance;
        return Physics.SphereCast(origin, radius, Vector3.down, out _, castDistance, groundMask, QueryTriggerInteraction.Ignore);
    }

    private void ReadMoveInput()
    {
        Vector2 move    = input != null ? input.Move : Vector2.zero;
        Vector3 forward = Vector3.forward;
        Vector3 right   = Vector3.right;

        if (cameraTransform != null)
        {
            forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.ProjectOnPlane(cameraTransform.up, Vector3.up);
            forward.Normalize();
            right = Vector3.Cross(Vector3.up, forward);
        }

        MoveDirection = Vector3.ClampMagnitude(forward * move.y + right * move.x, 1f);
        IsSprinting   = input != null && input.SprintHeld && HasMoveInput && !actionLocked;
    }

    private void UpdateJump()
    {
        if (input != null && input.JumpPressed)
            lastJumpPressedTime = Time.time;

        bool buffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canJump  = !actionLocked && !jumping && Time.time - lastGroundedTime <= coyoteTime;
        if (buffered && canJump)
            Jump();
    }

    public void Jump()
    {
        verticalVelocity    = Mathf.Sqrt(2f * jumpHeight * -gravity);
        jumping             = true;
        isGrounded          = false;
        lastGroundedTime    = -999f;
        lastJumpPressedTime = -999f;
        ignoreGroundUntil   = Time.time + 0.1f;

        if (animator != null)
        {
            animator.ResetTrigger(JumpHash);
            animator.SetTrigger(JumpHash);
        }
    }

    private void UpdatePlanarVelocity(float dt)
    {
        Vector3 target = Vector3.zero;
        if (!actionLocked)
            target = MoveDirection * (IsSprinting ? sprintSpeed : moveSpeed);

        float sharpness = actionLocked ? 20f : (isGrounded ? groundAcceleration : airAcceleration);
        planarVelocity  = Vector3.Lerp(planarVelocity, target, 1f - Mathf.Exp(-sharpness * dt));
    }

    private void UpdateRotation(float dt)
    {
        if (hasFacingOverride)
        {
            Quaternion target  = Quaternion.LookRotation(facingOverride);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, facingOverrideSpeed * dt);
        }
        else if (!actionLocked && HasMoveInput)
        {
            Quaternion target  = Quaternion.LookRotation(MoveDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-rotationSpeed * dt));
        }
    }

    private void UpdateGravity(float dt)
    {
        if (isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // mantém o personagem colado no chão (rampas/degraus)
        else
            verticalVelocity = Mathf.Max(verticalVelocity + gravity * dt, -50f);
    }

    private void UpdateAnimator(float dt)
    {
        if (animator == null)
            return;

        // Velocidade real (respeita paredes): sem "correr no lugar" ao bater num obstáculo.
        Vector3 velocity = controller.velocity;
        velocity.y = 0f;
        animator.SetFloat(SpeedHash, actionLocked ? 0f : velocity.magnitude, 0.08f, dt);

        // Pequena tolerância para degraus não dispararem a animação de queda.
        bool groundedForAnimation = isGrounded || (!jumping && Time.time - lastGroundedTime < 0.15f);
        animator.SetBool(GroundedHash, groundedForAnimation);
    }

    private void OnAnimatorMove()
    {
        if (animator == null || !actionLocked || rootMotionScale <= 0f)
            return;

        Vector3 delta = animator.deltaPosition;
        delta.y = 0f;
        if (delta.sqrMagnitude > 1e-8f)
            controller.Move(delta * rootMotionScale);
    }

    // ---- API usada pelo PlayerCombat ----

    public void SetActionLock(bool locked, float rootMotion = 0f)
    {
        actionLocked    = locked;
        rootMotionScale = locked ? rootMotion : 0f;
        if (!locked)
            hasFacingOverride = false;
    }

    public void SetFacing(Vector3 direction, float degreesPerSecond)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return;
        facingOverride      = direction.normalized;
        facingOverrideSpeed = degreesPerSecond;
        hasFacingOverride   = true;
    }

    public void ClearFacing()
    {
        hasFacingOverride = false;
    }

    public void SnapFacing(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(direction.normalized);
    }

    // Deslocamento extra respeitando colisões (ex.: avanço automático até o alvo do golpe).
    public void Nudge(Vector3 delta)
    {
        if (controller.enabled)
            controller.Move(delta);
    }

    public void Teleport(Vector3 position, Quaternion rotation)
    {
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
        planarVelocity     = Vector3.zero;
        verticalVelocity   = 0f;
    }
}
