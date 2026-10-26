using UnityEngine;

// Câmera orbital em terceira pessoa: o mouse gira, o scroll aproxima/afasta, ela colide
// com o cenário (não atravessa chão nem paredes), treme nos impactos e abre um pouco o
// campo de visão quando o personagem corre (sensação de velocidade).
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

    [Header("Velocidade")]
    [Tooltip("Graus a mais de campo de visão correndo na velocidade máxima. 0 desliga")]
    public float sprintFovBoost = 6f;
    [Tooltip("Quão rápido o campo de visão acompanha a velocidade")]
    public float fovSharpness = 4f;

    private static ThirdPersonCamera instance;

    private Camera cam;
    private float baseFov;
    private ThirdPersonController targetController;

    private float yaw;
    private float pitch = 15f;
    private float currentDistance;
    private Vector3 pivot;
    private Vector3 pivotVelocity;
    private float shakeTrauma;
    private readonly RaycastHit[] hits = new RaycastHit[16];

    public float Yaw => yaw;

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
            targetController = target.GetComponent<ThirdPersonController>();
        SetCursorLocked(true);
        SnapBehindTarget();
    }

    // Coloca a câmera atrás do alvo na hora, sem suavização (início, teleporte, respawn).
    public void SnapBehindTarget()
    {
        if (target == null)
            return;
        yaw             = target.eulerAngles.y;
        pivot           = target.position + Vector3.up * pivotHeight;
        pivotVelocity   = Vector3.zero;
        currentDistance = distance;
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        HandleCursor();
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            yaw     += Input.GetAxis("Mouse X") * sensitivity;
            pitch   -= Input.GetAxis("Mouse Y") * sensitivity * (invertY ? -1f : 1f);
            distance = Mathf.Clamp(distance - Input.GetAxis("Mouse ScrollWheel") * zoomSpeed, minDistance, maxDistance);
        }
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        // Tempo real: a câmera continua suave durante o hit stop.
        float dt = Time.unscaledDeltaTime;
        Vector3 desiredPivot = target.position + Vector3.up * pivotHeight;
        pivot = followSmoothTime > 0f
            ? Vector3.SmoothDamp(pivot, desiredPivot, ref pivotVelocity, followSmoothTime, Mathf.Infinity, dt)
            : desiredPivot;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 back        = rotation * Vector3.back;

        // Aproxima na hora (para nunca atravessar nada) e afasta suavemente.
        float allowed   = GetAllowedDistance(pivot, back, distance);
        currentDistance = allowed < currentDistance
            ? allowed
            : Mathf.Lerp(currentDistance, allowed, 1f - Mathf.Exp(-6f * dt));

        Vector3 position = pivot + back * currentDistance;

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
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, baseFov + boost, 1f - Mathf.Exp(-fovSharpness * dt));
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
