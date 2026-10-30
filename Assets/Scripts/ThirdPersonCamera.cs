using UnityEngine;

// Câmera orbital em terceira pessoa: o mouse gira, o scroll aproxima/afasta, ela colide
// com o cenário (não atravessa chão nem paredes), treme nos impactos e abre um pouco o
// campo de visão quando o personagem corre (sensação de velocidade).
// Com a mira travada (TargetLock) a câmera gira sozinha para enquadrar o player e o alvo: fica
// atrás do player olhando para o alvo, sobe/desce acompanhando um alvo lançado ao ar e se afasta
// quando os dois se separam. O mouse para cima/baixo ainda ajusta a altura.
// Esc solta o cursor; clicar na janela captura de novo.
public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 6f;
    public float minDistance = 2.5f;
    public float maxDistance = 10f;
    public float zoomSpeed = 4f;
    public float sensitivity = 3f;
    public bool invertY;
    public float minPitch = -30f;
    public float maxPitch = 70f;
    [Tooltip("Altura do ponto que a câmera orbita, acima dos pés do alvo")]
    public float pivotHeight = 1.9f;
    [Tooltip("Suavização do seguimento em segundos (0 = rígido)")]
    public float followSmoothTime = 0.05f;

    [Header("Colisão")]
    public LayerMask collisionMask = ~0;
    public float collisionRadius = 0.25f;
    public float collisionPadding = 0.1f;

    [Header("Tremor")]
    [Tooltip("Deslocamento máximo do tremor, em metros")]
    public float shakeMagnitude = 0.35f;
    [Tooltip("Quanto do tremor some por segundo")]
    public float shakeDecay = 2f;

    [Header("Mira travada")]
    [Tooltip("Tempo (s) para a câmera cobrir metade do giro até enquadrar o alvo")]
    public float lockOnHalflife = 0.12f;
    [Tooltip("Inclinação da câmera com a mira travada (graus, para baixo)")]
    public float lockOnPitch = 14f;
    [Tooltip("Quanto o ponto de órbita vai na direção do alvo (0 = no player, 1 = no alvo)")]
    [Range(0f, 0.6f)] public float lockOnPivotBias = 0.25f;
    [Tooltip("Distância extra por metro de separação entre player e alvo")]
    public float lockOnDistancePerMeter = 0.25f;
    public float lockOnMaxExtraDistance = 4f;

    [Header("Velocidade")]
    [Tooltip("Graus a mais de campo de visão correndo na velocidade máxima. 0 desliga")]
    public float sprintFovBoost = 10f;
    [Tooltip("Graus extras de campo de visão que \"estouram\" ao começar a disparada e voltam logo")]
    public float sprintFovPunch = 5f;
    [Tooltip("Disparando a câmera também se afasta um pouco (metros a mais na velocidade máxima)")]
    public float sprintDistanceBoost = 1.2f;
    [Tooltip("Quão rápido o campo de visão acompanha a velocidade")]
    public float fovSharpness = 4f;

    [Header("Disparada: a câmera acompanha a corrida")]
    [Tooltip("Quanto a câmera \"afunda\" a cada passo na disparada (m), sincronizado com o contato dos pés")]
    public float stepBob = 0.16f;
    [Tooltip("Balanço lateral alternado a cada passo (graus)")]
    public float stepRoll = 0.9f;
    [Tooltip("Vibração contínua na velocidade máxima (m)")]
    public float runVibration = 0.025f;
    public float runVibrationFrequency = 17f;
    [Tooltip("Inclinação da câmera para dentro das curvas: graus por (grau/s de giro) na velocidade máxima")]
    public float turnBank = 0.012f;
    public float maxTurnBank = 5f;
    [Tooltip("A câmera fica para trás ao acelerar e adianta ao frear (m por m/s²)")]
    public float accelerationLag = 0.03f;
    public float maxAccelerationLag = 0.9f;

    private static ThirdPersonCamera instance;

    private Camera cam;
    private float baseFov;
    private ThirdPersonController targetController;
    private TargetLock targetLock;
    private float pitchOffset;
    private float speedFactor;

    private AnimationEventSink targetSteps;
    private float bob, bobVelocity;
    private float stepSide = 1f;
    private float roll, rollTarget;
    private float bank;
    private float lastTargetYaw;
    private Vector3 lastTargetVelocity;
    private Vector3 lagOffset;
    private float fovPunch;
    private bool wasSprinting;

    private float yaw;
    private float pitch = 15f;
    private float currentDistance;
    private Vector3 pivot;
    private Vector3 pivotVelocity;
    private float shakeTrauma;
    private readonly RaycastHit[] hits = new RaycastHit[16];

    public float Yaw => yaw;
    // 0 = correndo ou mais devagar, 1 = disparada na velocidade máxima (usado pelos efeitos de velocidade)
    public float SpeedFactor => speedFactor;

    public static void Shake(float amount)
    {
        if (instance != null)
            instance.shakeTrauma = Mathf.Clamp01(instance.shakeTrauma + amount);
    }

    private void OnEnable()
    {
        instance = this;
    }

    private void OnDisable()
    {
        if (instance == this)
            instance = null;
    }

    private void Start()
    {
        cam = GetComponent<Camera>();
        if (cam != null)
            baseFov = cam.fieldOfView;
        if (target != null)
        {
            targetController = target.GetComponent<ThirdPersonController>();
            targetLock       = target.GetComponent<TargetLock>();
            targetSteps      = target.GetComponent<AnimationEventSink>();
            if (targetSteps != null)
                targetSteps.Footstep += OnFootstep;
            lastTargetYaw = target.eulerAngles.y;
        }
        SetCursorLocked(true);
        SnapBehindTarget();
    }

    private void OnDestroy()
    {
        if (targetSteps != null)
            targetSteps.Footstep -= OnFootstep;
    }

    // Cada passo na disparada dá um "tranco" para baixo e alterna o balanço lateral.
    private void OnFootstep()
    {
        if (speedFactor < 0.05f)
            return;
        bobVelocity -= stepBob * 14f * speedFactor;
        stepSide     = -stepSide;
        rollTarget   = stepSide * stepRoll * speedFactor;
    }

    // Apertar "travar" sem inimigo por perto: a câmera gira para trás do personagem.
    public static void RecenterBehindTarget()
    {
        if (instance != null && instance.target != null)
            instance.recenterUntil = Time.unscaledTime + 0.35f;
    }

    private float recenterUntil;

    // Coloca a câmera atrás do alvo na hora, sem suavização (início, teleporte, respawn).
    public void SnapBehindTarget()
    {
        if (target == null)
            return;
        yaw             = target.eulerAngles.y;
        pivot           = target.position + Vector3.up * pivotHeight;
        pivotVelocity   = Vector3.zero;
        currentDistance = distance;
        lagOffset       = Vector3.zero;
        bob = bobVelocity = roll = rollTarget = bank = 0f;
        lastTargetYaw   = target.eulerAngles.y;
        if (targetController != null)
            lastTargetVelocity = targetController.PlanarVelocity;
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        HandleCursor();
        // Tempo real: a câmera continua suave durante o hit stop.
        float dt = Time.unscaledDeltaTime;
        bool locked = targetLock != null && targetLock.IsLocked;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            float mouseY = Input.GetAxis("Mouse Y") * sensitivity * (invertY ? -1f : 1f);
            if (locked)
                pitchOffset = Mathf.Clamp(pitchOffset - mouseY, -20f, 35f); // o mouse X troca de alvo (TargetLock)
            else
            {
                yaw   += Input.GetAxis("Mouse X") * sensitivity;
                pitch -= mouseY;
            }
            distance = Mathf.Clamp(distance - Input.GetAxis("Mouse ScrollWheel") * zoomSpeed, minDistance, maxDistance);
        }

        Vector3 desiredPivot = target.position + Vector3.up * pivotHeight;
        float frameDistance  = distance;
        if (locked)
        {
            // Atrás do player, olhando para o alvo
            Vector3 aim     = targetLock.TargetPoint;
            Vector3 toAim   = aim - desiredPivot;
            Vector3 flat    = new Vector3(toAim.x, 0f, toAim.z);
            float separation = flat.magnitude;
            float blend     = 1f - Mathf.Exp(-0.693f * dt / Mathf.Max(0.01f, lockOnHalflife));
            if (separation > 0.3f)
            {
                float wantedYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                yaw = Mathf.LerpAngle(yaw, wantedYaw, blend);
            }
            // Alvo acima (lançado): a câmera abaixa para olhar para cima; abaixo: sobe
            float elevation = Mathf.Atan2(toAim.y, Mathf.Max(separation, 1f)) * Mathf.Rad2Deg;
            float wantedPitch = lockOnPitch - elevation * 0.7f + pitchOffset;
            pitch = Mathf.LerpAngle(pitch, wantedPitch, blend);
            desiredPivot += Vector3.Lerp(Vector3.zero, toAim, lockOnPivotBias);
            frameDistance += Mathf.Min(separation * lockOnDistancePerMeter, lockOnMaxExtraDistance);
        }
        else
        {
            pitchOffset = Mathf.MoveTowards(pitchOffset, 0f, dt * 40f);
            if (Time.unscaledTime < recenterUntil)
                yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, 1f - Mathf.Exp(-12f * dt));
        }
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        if (targetController != null)
        {
            Vector3 v = targetController.Controller.velocity;
            v.y = 0f;
            float wanted = Mathf.InverseLerp(targetController.moveSpeed, targetController.sprintSpeed, v.magnitude);
            speedFactor  = Mathf.Lerp(speedFactor, wanted, 1f - Mathf.Exp(-2.5f * dt));
            frameDistance += sprintDistanceBoost * speedFactor;
            UpdateSpeedMotion(dt);
        }

        pivot = followSmoothTime > 0f
            ? Vector3.SmoothDamp(pivot, desiredPivot + lagOffset, ref pivotVelocity, followSmoothTime, Mathf.Infinity, dt)
            : desiredPivot + lagOffset;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, bank + roll);
        Vector3 back        = rotation * Vector3.back;

        // Aproxima na hora (para nunca atravessar nada) e afasta suavemente.
        float allowed   = GetAllowedDistance(pivot, back, frameDistance);
        currentDistance = allowed < currentDistance
            ? allowed
            : Mathf.Lerp(currentDistance, allowed, 1f - Mathf.Exp(-6f * dt));

        Vector3 position = pivot + back * currentDistance;

        // Disparada: tranco de cada passo + vibração (mais forte quanto mais rápido)
        position += rotation * Vector3.up * bob;
        if (speedFactor > 0.01f && runVibration > 0f)
        {
            float t = Time.unscaledTime * runVibrationFrequency;
            Vector3 vibration = new Vector3(Mathf.PerlinNoise(t, 0.17f) - 0.5f, Mathf.PerlinNoise(0.53f, t) - 0.5f, 0f);
            position += rotation * vibration * (2f * runVibration * speedFactor * speedFactor);
        }

        if (shakeTrauma > 0f)
        {
            float t       = Time.unscaledTime * 30f;
            Vector3 noise = new Vector3(Mathf.PerlinNoise(t, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, t) - 0.5f, 0f);
            position     += rotation * noise * (2f * shakeTrauma * shakeMagnitude);
            rotation     *= Quaternion.Euler(0f, 0f, (Mathf.PerlinNoise(t, t) - 0.5f) * 4f * shakeTrauma);
            shakeTrauma   = Mathf.Max(0f, shakeTrauma - shakeDecay * dt);
        }

        transform.SetPositionAndRotation(position, rotation);
        UpdateFieldOfView(dt);
    }

    // Movimentos da câmera que vêm da velocidade do personagem (fora o tranco dos passos, que vem dos eventos).
    private void UpdateSpeedMotion(float dt)
    {
        // Mola do tranco dos passos (um pouco menos que criticamente amortecida: afunda e volta com um leve repique)
        bobVelocity += (-220f * bob - 22f * bobVelocity) * dt;
        bob         += bobVelocity * dt;
        rollTarget   = Mathf.MoveTowards(rollTarget, 0f, stepRoll * 2.5f * dt);
        roll         = Mathf.Lerp(roll, rollTarget, 1f - Mathf.Exp(-12f * dt));

        // Inclinação nas curvas: proporcional ao giro do personagem e à velocidade
        float yawNow  = target.eulerAngles.y;
        float yawRate = Time.deltaTime > 0f ? Mathf.DeltaAngle(lastTargetYaw, yawNow) / Time.deltaTime : 0f;
        lastTargetYaw = yawNow;
        float bankTarget = Mathf.Clamp(-yawRate * turnBank, -maxTurnBank, maxTurnBank) * speedFactor;
        bank = Mathf.Lerp(bank, bankTarget, 1f - Mathf.Exp(-4f * dt));

        // Atraso de aceleração: a câmera "fica para trás" na arrancada e adianta na freada
        Vector3 velocity = targetController.PlanarVelocity;
        if (Time.deltaTime > 0f)
        {
            Vector3 acceleration = (velocity - lastTargetVelocity) / Time.deltaTime;
            Vector3 wantedLag    = Vector3.ClampMagnitude(-acceleration * accelerationLag, maxAccelerationLag);
            lagOffset = Vector3.Lerp(lagOffset, wantedLag, 1f - Mathf.Exp(-5f * dt));
        }
        lastTargetVelocity = velocity;

        // "Estouro" do campo de visão ao engatar a disparada
        bool sprinting = targetController.IsSprinting && velocity.magnitude > targetController.moveSpeed * 0.8f;
        if (sprinting && !wasSprinting)
            fovPunch = sprintFovPunch;
        wasSprinting = sprinting;
        fovPunch = Mathf.MoveTowards(fovPunch, 0f, sprintFovPunch * 1.4f * dt);
    }

    private void UpdateFieldOfView(float dt)
    {
        if (cam == null || sprintFovBoost <= 0f)
            return;

        float boost = 0f;
        if (targetController != null)
        {
            Vector3 velocity = targetController.Controller.velocity;
            velocity.y = 0f;
            boost = sprintFovBoost * Mathf.InverseLerp(targetController.moveSpeed, targetController.sprintSpeed, velocity.magnitude);
        }
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, baseFov + boost + fovPunch, 1f - Mathf.Exp(-fovSharpness * dt));
    }

    private float GetAllowedDistance(Vector3 origin, Vector3 direction, float maxDistance)
    {
        int count     = Physics.SphereCastNonAlloc(origin, collisionRadius, direction, hits, maxDistance, collisionMask, QueryTriggerInteraction.Ignore);
        float allowed = maxDistance;

        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hits[i];
            // distance 0 = esfera já começou encostando; ignorar evita a câmera "colapsar"
            if (hit.distance <= 0f || IsIgnored(hit.collider))
                continue;
            allowed = Mathf.Min(allowed, hit.distance - collisionPadding);
        }
        return Mathf.Max(allowed, 0.5f);
    }

    private bool IsIgnored(Collider col)
    {
        // Personagens (player, bonecos) e objetos soltos (espada caída) não empurram a câmera.
        if (col.transform.IsChildOf(target.root))
            return true;
        if (col.GetComponentInParent<Health>() != null)
            return true;
        Rigidbody body = col.attachedRigidbody;
        return body != null && !body.isKinematic;
    }

    private void HandleCursor()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            SetCursorLocked(false);
        else if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0) && Application.isFocused)
            SetCursorLocked(true);
    }

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !locked;
    }
}
