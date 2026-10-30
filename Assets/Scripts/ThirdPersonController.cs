using System;
using MotionMatching;
using UnityEngine;

// Locomoção em terceira pessoa: anda/corre relativo à câmera, pula (com pulo duplo) e aplica gravidade.
//
// A velocidade e a rotação seguem o input por molas criticamente amortecidas (a "simulação" do
// Motion Matching): aceleram e freiam de forma suave e previsível, e a mesma mola prevê a
// trajetória do próximo segundo, usada pelo MotionMatchingLocomotion para escolher a animação.
// Durante ações do PlayerCombat (golpes e esquiva) o input de movimento é ignorado e o
// deslocamento vem da própria animação (root motion), aplicado em OnAnimatorMove.
// O tronco inclina para dentro das curvas e para frente/trás ao acelerar e frear.
// Com a mira travada (TargetLock) o personagem fica de frente para o alvo e anda de lado/para trás
// (strafe); disparar (Shift) solta a orientação e corre livre.
// Golpes aéreos deixam a gravidade fraca (o personagem "flutua" no combo) e o lançador sobe junto
// com a animação (deslocamento vertical do root motion).
[RequireComponent(typeof(CharacterController))]
public class ThirdPersonController : MonoBehaviour
{
    [Header("Movimento")]
    [Tooltip("Andando (Alt segurado, Caps Lock alterna, ou direcional analógico pouco inclinado)")]
    public float walkSpeed = 2.4f;
    [Tooltip("Correndo (padrão)")]
    public float moveSpeed = 7f;
    [Tooltip("Disparada (Shift): a corrida a toda velocidade")]
    public float sprintSpeed = 12.5f;
    [Tooltip("Segurando a disparada, a velocidade continua subindo até este valor (m/s)")]
    public float sprintTopSpeed = 13.8f;
    [Tooltip("Tempo (s) de disparada contínua para chegar à velocidade máxima")]
    public float sprintRampTime = 1.5f;
    [Tooltip("Meia-vida da aceleração no arranque da disparada (menor = arrancada mais explosiva)")]
    public float sprintBurstHalflife = 0.07f;
    [Tooltip("Duração (s) da arrancada explosiva")]
    public float sprintBurstDuration = 0.35f;
    [Tooltip("Com a mira travada: velocidade andando de lado/para trás de frente para o alvo")]
    public float strafeSpeed = 4.8f;
    [Tooltip("Direcional analógico abaixo disto anda em vez de correr")]
    [Range(0.1f, 1f)] public float analogWalkThreshold = 0.6f;
    [Tooltip("Tempo (s) para a velocidade cobrir metade do caminho até a desejada. Menor = arranca e freia mais seco")]
    public float velocityHalflife = 0.15f;
    [Tooltip("Mesma ideia, no ar (maior = menos controle durante o pulo)")]
    public float airVelocityHalflife = 0.35f;
    [Tooltip("Tempo (s) para o giro cobrir metade do ângulo até a direção desejada")]
    public float rotationHalflife = 0.12f;

    [Header("Pulo e gravidade")]
    public float gravity = -22f;
    public float jumpHeight = 1.6f;
    [Tooltip("Tempo depois de sair de uma borda em que ainda dá para pular")]
    public float coyoteTime = 0.12f;
    [Tooltip("Pulo duplo (Espaço no ar)")]
    public bool doubleJump = true;
    public float doubleJumpHeight = 1.5f;
    [Tooltip("Estado do Animator do pulo duplo")]
    public string doubleJumpState = "DoubleJump";
    [Tooltip("Apertar pulo um pouco antes de tocar o chão ainda conta")]
    public float jumpBufferTime = 0.15f;
    public float groundCheckDistance = 0.2f;
    public LayerMask groundMask = ~0;
    [Tooltip("Pousos acima desta velocidade (m/s) tremem a câmera; um pulo normal pousa a ~8 m/s")]
    public float hardLandingSpeed = 10f;
    [Tooltip("Tremor de câmera de um pouso bem forte")]
    public float landingShake = 0.25f;

    [Header("Inclinação do tronco")]
    [Tooltip("Inclinação máxima do tronco para dentro da curva (graus). 0 desliga")]
    public float maxLean = 8f;
    [Tooltip("Graus de inclinação por (grau/s de giro × m/s de velocidade)")]
    public float leanAmount = 0.0015f;
    [Tooltip("Inclinação máxima para frente/trás ao acelerar/frear (graus). 0 desliga")]
    public float maxAccelerationLean = 6f;
    [Tooltip("Graus de inclinação por m/s² de aceleração")]
    public float accelerationLeanAmount = 0.35f;
    [Tooltip("Inclinação extra do tronco para frente na disparada (graus, na velocidade máxima)")]
    public float sprintLean = 5f;
    [Tooltip("Quão rápido a inclinação acompanha (maior = mais seco)")]
    public float leanSharpness = 10f;

    [Header("Referências")]
    public Transform cameraTransform;
    public PlayerInputReader input;

    private CharacterController controller;
    private Animator animator;
    private Vector3 planarVelocity;
    private Vector3 planarAcceleration;
    private Vector3 desiredVelocity;
    private Quaternion desiredRotation = Quaternion.identity;
    private Vector3 angularVelocity;
    private float verticalVelocity;
    private bool isGrounded;
    private bool jumping;
    private bool walkToggled;
    private bool usedDoubleJump;
    private float hangUntil;
    private float hangGravityScale = 1f;
    private float lastGroundedTime    = -999f;
    private float lastJumpPressedTime = -999f;
    private float ignoreGroundUntil;
    private Vector3 wallNormal;
    private float wallContactTime = -999f;
    private float sprintTime;

    // Estado controlado pelo PlayerCombat
    private bool actionLocked;
    private float rootMotionScale;
    private bool verticalRootMotion;
    private bool hasFacingOverride;
    private Vector3 facingOverride;
    private float facingOverrideSpeed;

    // Inclinação do tronco
    private Transform leanBone;
    private float lean;
    private float pitchLean;
    private float lastYaw;

    private static readonly int SpeedHash    = Animator.StringToHash("Speed");
    private static readonly int JumpHash     = Animator.StringToHash("Jump");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");

    public Animator Animator => animator;
    public CharacterController Controller => controller;
    public bool IsGrounded => isGrounded;
    public bool IsActionLocked => actionLocked;
    public float VerticalVelocity => verticalVelocity;
    // Alvo da mira travada: o personagem fica de frente para ele (definido pelo TargetLock)
    public Transform LockTarget { get; set; }
    public bool IsStrafing => LockTarget != null && !IsSprinting;
    // Multiplicador da gravidade (golpes aéreos deixam o personagem quase parado no ar)
    public float GravityScale { get; set; } = 1f;
    // Direção desejada relativa à câmera (magnitude 0..1)
    public Vector3 MoveDirection { get; private set; }
    public bool HasMoveInput => MoveDirection.sqrMagnitude > 0.01f;
    public bool IsSprinting { get; private set; }
    public bool IsWalking { get; private set; }
    // Velocidade da disparada agora (sobe de sprintSpeed até sprintTopSpeed enquanto o Shift fica apertado)
    public float CurrentSprintSpeed => Mathf.Lerp(sprintSpeed, Mathf.Max(sprintSpeed, sprintTopSpeed),
                                                  sprintRampTime > 0f ? Mathf.Clamp01(sprintTime / sprintRampTime) : 1f);
    // Velocidade/rotação que o input pede agora (alvo das molas)
    public Vector3 DesiredVelocity => desiredVelocity;
    public Quaternion DesiredRotation => desiredRotation;
    public Vector3 PlanarVelocity => planarVelocity;
    // No chão, sem golpe/esquiva/pulo: a locomoção (andar, correr, parado) é livre
    public bool InFreeLocomotion => isGrounded && !jumping && !actionLocked;

    // Ajuste do deslocamento das ações antes de aplicá-lo (o PlayerCombat encurta o passo perto do
    // alvo, soma o embalo dos ataques em corrida e empurra o alvo quando encosta).
    public Func<Vector3, Vector3> ActionMotionFilter { get; set; }
    // Disparado ao tocar o chão depois de um pulo/queda, com a velocidade vertical do impacto (m/s).
    public event Action<float> Landed;
    public event Action DoubleJumped;

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
            if (animator.isHuman)
                leanBone = animator.GetBoneTransform(HumanBodyBones.Spine);
        }
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
        lastYaw         = transform.eulerAngles.y;
        desiredRotation = transform.rotation;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        UpdateGrounded();
        ReadMoveInput();
        UpdateJump();
        UpdateDesired();
        Vector3 planarDelta = UpdatePlanarVelocity(dt);
        UpdateRotation(dt);
        UpdateGravity(dt);

        controller.Move(planarDelta + Vector3.up * (verticalVelocity * dt));
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
        usedDoubleJump   = false;
        hangUntil        = 0f;
        if (!wasGrounded)
        {
            // Velocidade vertical ainda é a da queda: a gravidade só "cola" no chão depois deste passo.
            float impact = -verticalVelocity;
            if (impact > 1f)
                Landed?.Invoke(impact);
            if (impact > hardLandingSpeed)
                ThirdPersonCamera.Shake(landingShake * Mathf.Clamp01((impact - hardLandingSpeed) / hardLandingSpeed));
        }
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

        if (input != null && input.WalkTogglePressed)
            walkToggled = !walkToggled;
        bool walkRequested = walkToggled || (input != null && input.WalkHeld) || move.magnitude < analogWalkThreshold;
        IsSprinting = input != null && input.SprintHeld && HasMoveInput && !actionLocked;
        IsWalking   = HasMoveInput && !IsSprinting && walkRequested;
        // A disparada "engata" no chão; no ar o tempo para de contar, mas não zera (pular não perde o embalo)
        if (!IsSprinting)
            sprintTime = 0f;
        else if (isGrounded)
            sprintTime += Time.deltaTime;
    }

    private void UpdateJump()
    {
        if (input != null && input.JumpPressed)
            lastJumpPressedTime = Time.time;

        bool buffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canJump  = !actionLocked && !jumping && Time.time - lastGroundedTime <= coyoteTime;
        if (buffered && canJump)
            Jump();
        else if (buffered && doubleJump && !actionLocked && !isGrounded && !usedDoubleJump
                 && Time.time - lastGroundedTime > coyoteTime)
            DoubleJump();
    }

    // Segundo pulo, no ar: zera a queda, sobe de novo e permite mudar de direção na hora.
    public void DoubleJump()
    {
        usedDoubleJump      = true;
        jumping             = true;
        lastJumpPressedTime = -999f;
        hangUntil           = 0f;
        verticalVelocity    = Mathf.Sqrt(2f * doubleJumpHeight * -gravity);
        if (HasMoveInput)
        {
            float speed = Mathf.Max(planarVelocity.magnitude, IsWalking ? walkSpeed : moveSpeed);
            planarVelocity  = MoveDirection.normalized * speed;
            desiredRotation = Quaternion.LookRotation(MoveDirection);
        }
        if (animator != null && !string.IsNullOrEmpty(doubleJumpState))
            animator.CrossFadeInFixedTime(doubleJumpState, 0.08f, 0, 0f);
        DoubleJumped?.Invoke();
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

    // Velocidade e rotação que o input pede neste frame (os alvos das molas)
    private void UpdateDesired()
    {
        desiredVelocity = Vector3.zero;
        bool strafing = !actionLocked && IsStrafing;
        if (strafing)
        {
            // Mira travada: de frente para o alvo, com ou sem input
            Vector3 toTarget = LockTarget.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.04f)
                desiredRotation = Quaternion.LookRotation(toTarget);
        }
        if (!actionLocked && HasMoveInput)
        {
            float speed = IsSprinting ? CurrentSprintSpeed : (IsWalking ? walkSpeed : (strafing ? strafeSpeed : moveSpeed));
            desiredVelocity = MoveDirection.normalized * speed;
            if (!strafing)
                desiredRotation = Quaternion.LookRotation(MoveDirection);

            // Encostado numa parede: desliza ao longo dela em vez de "correr no lugar"
            if (Time.time - wallContactTime < 0.15f)
            {
                float into = Vector3.Dot(desiredVelocity, wallNormal);
                if (into < 0f)
                    desiredVelocity -= wallNormal * into;
            }
        }
    }

    private Vector3 UpdatePlanarVelocity(float dt)
    {
        float groundHalflife = IsSprinting && sprintTime < sprintBurstDuration ? sprintBurstHalflife : velocityHalflife;
        float halflife = actionLocked ? 0.05f : (isGrounded ? groundHalflife : airVelocityHalflife);
        Vec3 position = Vec3.Zero;
        Vec3 velocity = ToVec(planarVelocity);
        Vec3 acceleration = ToVec(planarAcceleration);
        Springs.SimulationPositionsUpdate(ref position, ref velocity, ref acceleration, ToVec(desiredVelocity), halflife, dt);
        planarVelocity     = ToVector(velocity);
        planarAcceleration = ToVector(acceleration);
        Vector3 delta = ToVector(position);
        delta.y = 0f;
        return delta;
    }

    private void UpdateRotation(float dt)
    {
        if (hasFacingOverride)
        {
            Quaternion target  = Quaternion.LookRotation(facingOverride);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, facingOverrideSpeed * dt);
            angularVelocity    = Vector3.zero;
            desiredRotation    = transform.rotation;
        }
        else if (!actionLocked)
        {
            Quat rotation = ToQuat(transform.rotation);
            Vec3 angular  = ToVec(angularVelocity);
            Springs.SimpleSpringDamperExact(ref rotation, ref angular, ToQuat(desiredRotation), rotationHalflife, dt);
            // só gira em torno do eixo vertical
            angular.x = 0f;
            angular.z = 0f;
            angularVelocity = ToVector(angular);
            Vector3 forward = ToVector(rotation * Vec3.Forward);
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(forward);
        }
        else
        {
            angularVelocity = Vector3.zero;
        }
    }

    private void UpdateGravity(float dt)
    {
        if (verticalRootMotion)
        {
            // A altura vem da animação (OnAnimatorMove)
            verticalVelocity = 0f;
            return;
        }
        if (isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // mantém o personagem colado no chão (rampas/degraus)
        else
        {
            float scale = GravityScale;
            if (Time.time < hangUntil)
                scale = Mathf.Min(scale, hangGravityScale);
            // Flutuando no combo aéreo a queda também é contida (não acumula velocidade de queda)
            float terminal = scale < 0.99f ? Mathf.Lerp(-6f, -50f, scale) : -50f;
            verticalVelocity = Mathf.Max(verticalVelocity + gravity * scale * dt, Mathf.Min(terminal, verticalVelocity));
        }
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

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // Paredes (não o chão): lembradas por um instante para o input deslizar ao longo delas
        if (hit.normal.y < 0.3f)
        {
            Vector3 n = hit.normal;
            n.y = 0f;
            if (n.sqrMagnitude > 1e-4f)
            {
                wallNormal      = n.normalized;
                wallContactTime = Time.time;
            }
        }
    }

    private void OnAnimatorMove()
    {
        if (animator == null || !actionLocked)
            return;

        Vector3 delta = animator.deltaPosition * rootMotionScale;
        float rise = verticalRootMotion ? animator.deltaPosition.y : 0f;
        delta.y = 0f;
        if (ActionMotionFilter != null)
            delta = ActionMotionFilter(delta);
        delta.y = rise;
        if (delta.sqrMagnitude > 1e-8f && controller.enabled)
            controller.Move(delta);
    }

    private void LateUpdate()
    {
        UpdateLean(Time.deltaTime);
    }

    // Inclina a coluna para dentro da curva (proporcional ao giro e à velocidade) e para frente/trás
    // ao acelerar/frear. Roda depois da animação deste frame (e é refeito todo frame), então nunca acumula.
    private void UpdateLean(float dt)
    {
        if (dt <= 0f)
            return;

        float yaw     = transform.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(lastYaw, yaw) / dt; // graus/s, positivo = virando para a direita
        lastYaw       = yaw;

        Vector3 velocity = controller.velocity;
        velocity.y = 0f;
        bool free = !actionLocked && isGrounded;
        float target = !free || maxLean <= 0f
            ? 0f
            : Mathf.Clamp(-yawRate * velocity.magnitude * leanAmount, -maxLean, maxLean);
        float pitchTarget = !free || maxAccelerationLean <= 0f
            ? 0f
            : Mathf.Clamp(Vector3.Dot(planarAcceleration, transform.forward) * accelerationLeanAmount,
                          -maxAccelerationLean, maxAccelerationLean);
        // Postura de disparada: tronco jogado para frente, proporcional à velocidade acima da corrida
        if (free)
            pitchTarget += sprintLean * Mathf.InverseLerp(moveSpeed, sprintSpeed, velocity.magnitude);
        float blend = 1f - Mathf.Exp(-leanSharpness * dt);
        lean      = Mathf.Lerp(lean, target, blend);
        pitchLean = Mathf.Lerp(pitchLean, pitchTarget, blend);

        if (leanBone != null && (Mathf.Abs(lean) > 0.01f || Mathf.Abs(pitchLean) > 0.01f))
            leanBone.rotation = Quaternion.AngleAxis(lean, transform.forward)
                                * Quaternion.AngleAxis(pitchLean, transform.right) * leanBone.rotation;
    }

    // Trajetória prevista pelas mesmas molas (posições/rotações daqui a times[i] segundos),
    // dividida por 'scale' (unidades do esqueleto).
    public void PredictTrajectory(float[] times, Vec3[] positions, Quat[] rotations, float scale = 1f)
    {
        Vec3 start   = ToVec(transform.position) / scale;
        Vec3 desired = ToVec(desiredVelocity) / scale;
        Quat goal    = ToQuat(desiredRotation);
        for (int i = 0; i < times.Length; i++)
        {
            Vec3 p = start, v = ToVec(planarVelocity) / scale, a = ToVec(planarAcceleration) / scale;
            Springs.SimulationPositionsUpdate(ref p, ref v, ref a, desired, velocityHalflife, times[i]);
            Quat r = ToQuat(transform.rotation);
            Vec3 w = ToVec(angularVelocity);
            Springs.SimpleSpringDamperExact(ref r, ref w, goal, rotationHalflife, times[i]);
            positions[i] = p;
            rotations[i] = r;
        }
    }

    // ---- API usada pelo PlayerCombat ----

    public void SetActionLock(bool locked, float rootMotion = 0f, bool vertical = false)
    {
        actionLocked       = locked;
        rootMotionScale    = locked ? rootMotion : 0f;
        verticalRootMotion = locked && vertical;
        if (verticalRootMotion)
            LeaveGround();
        if (!locked)
        {
            GravityScale = 1f;
            hasFacingOverride = false;
            // volta a seguir o input a partir da direção atual (sem "voltar" para a de antes do golpe)
            desiredRotation = transform.rotation;
            angularVelocity = Vector3.zero;
        }
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
        {
            transform.rotation = Quaternion.LookRotation(direction.normalized);
            desiredRotation    = transform.rotation;
            angularVelocity    = Vector3.zero;
        }
    }

    // Tira o personagem do chão (lançador, golpes que sobem): o chão só volta a contar daqui a pouco.
    public void LeaveGround(float seconds = 0.15f)
    {
        isGrounded        = false;
        jumping           = true;
        lastGroundedTime  = -999f;
        ignoreGroundUntil = Time.time + seconds;
    }

    public void SetVerticalVelocity(float velocity)
    {
        verticalVelocity = velocity;
        if (velocity > 0f)
            LeaveGround(0.1f);
    }

    // Gravidade reduzida por um tempo (ex.: logo depois do lançador, para dar tempo de emendar o combo aéreo).
    public void Hang(float seconds, float gravityScale)
    {
        hangUntil        = Time.time + seconds;
        hangGravityScale = gravityScale;
        if (verticalVelocity < 0f)
            verticalVelocity = 0f;
    }

    // Deslocamento extra respeitando colisões (ex.: avanço automático até o alvo do golpe).
    public void Nudge(Vector3 delta)
    {
        if (controller.enabled)
            controller.Move(delta);
    }

    // Ao sair de uma ação que vinha se deslocando (ex.: ataque em corrida), segue com essa
    // velocidade: com o direcional apertado continua correndo, solto desacelera normalmente.
    public void SetMomentum(Vector3 velocity)
    {
        velocity.y         = 0f;
        planarVelocity     = velocity;
        planarAcceleration = Vector3.zero;
        if (animator != null)
            animator.SetFloat(SpeedHash, velocity.magnitude);
    }

    public void Teleport(Vector3 position, Quaternion rotation)
    {
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
        planarVelocity     = Vector3.zero;
        planarAcceleration = Vector3.zero;
        angularVelocity    = Vector3.zero;
        verticalVelocity   = 0f;
        desiredRotation    = rotation;
        lastYaw            = rotation.eulerAngles.y;
        lean               = 0f;
        pitchLean          = 0f;
    }

    private static Vec3 ToVec(Vector3 v) { return new Vec3(v.x, v.y, v.z); }
    private static Vector3 ToVector(Vec3 v) { return new Vector3(v.x, v.y, v.z); }
    private static Quat ToQuat(Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }
}
