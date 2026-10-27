using UnityEngine;
using UnityEngine.Rendering;

// HUD da mira travada: prismas triangulares girando em círculos em volta do alvo.
//   - anel externo: prismas apontando para o centro (a "mira"), girando devagar;
//   - anel interno: prismas menores deitados ao longo do círculo, girando rápido no sentido
//     contrário, como setas correndo em volta do alvo;
//   - cada prisma também gira no próprio eixo; as faces têm brilhos diferentes (cor por vértice),
//     então o giro aparece como um brilho facetado.
// Ao travar, os anéis fecham de longe com um leve "quique"; ao trocar de alvo deslizam até o novo;
// a cada golpe que acerta o alvo dão um pulso; ao destravar abrem e somem.
// Os anéis ficam de frente para a câmera e um pouco à frente do alvo, para o corpo não os esconder.
[DefaultExecutionOrder(200)] // depois da câmera
public class LockOnIndicator : MonoBehaviour
{
    public TargetLock targetLock;
    public PlayerCombat combat;
    [Tooltip("Material transparente com cor por vértice (URP Particles/Unlit)")]
    public Material material;
    public Color color = new Color(1f, 0.74f, 0.2f, 1f);
    public Color pulseColor = new Color(1f, 0.97f, 0.85f, 1f);

    [Header("Anel externo")]
    public int outerCount = 6;
    public float outerRadius = 1.35f;
    [Tooltip("Graus por segundo")]
    public float outerSpeed = 55f;
    public float outerLength = 0.55f;

    [Header("Anel interno")]
    public int innerCount = 3;
    public float innerRadius = 0.95f;
    public float innerSpeed = -150f;
    public float innerLength = 0.38f;

    [Header("Movimento")]
    [Tooltip("Giro de cada prisma no próprio eixo (graus/s)")]
    public float spinSpeed = 300f;
    [Tooltip("Os anéis ficam este tanto à frente do alvo, na direção da câmera")]
    public float towardCamera = 0.9f;
    [Tooltip("Distância da câmera em que os anéis têm o tamanho nominal (mais longe crescem um pouco para continuar legíveis)")]
    public float referenceDistance = 9f;
    public float acquireTime = 0.35f;
    public float releaseTime = 0.25f;

    private Transform root;
    private Transform[] outer;
    private Transform[] inner;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private Vector3 point;
    private float visibility;   // 0..1
    private float appearT = 1f; // progresso da animação de travar
    private float pulse;
    private float switchT = 1f;
    private bool showing;
    private float angleOuter, angleInner, spin;

    private void Awake()
    {
        if (targetLock == null)
            targetLock = FindFirstObjectByType<TargetLock>();
        if (combat == null && targetLock != null)
            combat = targetLock.GetComponent<PlayerCombat>();
        block = new MaterialPropertyBlock();
        Build();
        root.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (targetLock != null)
        {
            targetLock.Locked   += OnLocked;
            targetLock.Switched += OnSwitched;
        }
        if (combat != null)
            combat.HitLanded += OnHit;
    }

    private void OnDisable()
    {
        if (targetLock != null)
        {
            targetLock.Locked   -= OnLocked;
            targetLock.Switched -= OnSwitched;
        }
        if (combat != null)
            combat.HitLanded -= OnHit;
    }

    private void OnLocked(Health target)
    {
        point   = TargetLock.AimPoint(target);
        appearT = 0f;
        switchT = 1f;
        pulse   = 0.6f;
    }

    private void OnSwitched(Health target)
    {
        switchT = 0f;
        pulse   = 0.5f;
    }

    private void OnHit(DamageInfo info, Health target)
    {
        if (targetLock != null && target == targetLock.Target)
            pulse = Mathf.Max(pulse, info.heavy ? 1f : 0.55f);
    }

    // ---- Montagem ----

    private void Build()
    {
        root = new GameObject("LockOn Prisms").transform;
        root.SetParent(transform, false);
        Mesh prism = CreatePrism();
        outer = new Transform[outerCount];
        inner = new Transform[innerCount];
        renderers = new Renderer[outerCount + innerCount];
        for (int i = 0; i < outerCount; i++)
            outer[i] = CreatePart("Outer " + i, prism, i);
        for (int i = 0; i < innerCount; i++)
            inner[i] = CreatePart("Inner " + i, prism, outerCount + i);
    }

    private Transform CreatePart(string partName, Mesh mesh, int index)
    {
        var go = new GameObject(partName, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(root, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial       = material;
        r.shadowCastingMode    = ShadowCastingMode.Off;
        r.receiveShadows       = false;
        r.lightProbeUsage      = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderers[index] = r;
        return go.transform;
    }

    // Prisma triangular de comprimento 1 ao longo de +Z (base em z = 0) com ponta piramidal em z = 1.
    // Cada face tem seus próprios vértices e um brilho diferente na cor do vértice.
    private static Mesh CreatePrism()
    {
        const float body = 0.68f;
        const float half = 0.21f; // meia largura relativa ao comprimento
        var corners = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            float a = Mathf.PI * 0.5f + i * Mathf.PI * 2f / 3f;
            corners[i] = new Vector3(Mathf.Cos(a) * half, Mathf.Sin(a) * half, 0f);
        }
        Vector3 tip = new Vector3(0f, 0f, 1f);
        var vertices = new System.Collections.Generic.List<Vector3>();
        var colors   = new System.Collections.Generic.List<Color>();
        var indices  = new System.Collections.Generic.List<int>();
        float[] sideShade = { 1f, 0.8f, 0.62f };
        float[] tipShade  = { 1f, 0.9f, 0.74f };

        void Face(Vector3[] pts, float shade)
        {
            int start = vertices.Count;
            foreach (Vector3 p in pts)
            {
                vertices.Add(p);
                colors.Add(new Color(shade, shade, shade, 1f));
            }
            for (int k = 1; k < pts.Length - 1; k++)
            {
                indices.Add(start);
                indices.Add(start + k);
                indices.Add(start + k + 1);
            }
        }

        for (int i = 0; i < 3; i++)
        {
            Vector3 a = corners[i], b = corners[(i + 1) % 3];
            Vector3 a1 = a + Vector3.forward * body, b1 = b + Vector3.forward * body;
            Face(new[] { a, a1, b1, b }, sideShade[i]);   // lado do corpo
            Face(new[] { a1, tip, b1 }, tipShade[i]);     // ponta
        }
        Face(new[] { corners[0], corners[2], corners[1] }, 0.5f); // base

        var mesh = new Mesh { name = "LockOnPrism", hideFlags = HideFlags.DontSave };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---- Atualização ----

    private void LateUpdate()
    {
        float dt   = Time.unscaledDeltaTime;
        Camera cam = Camera.main;
        Health target = targetLock != null ? targetLock.Target : null;

        if (target != null)
        {
            showing = true;
            Vector3 aim = TargetLock.AimPoint(target);
            // Trocando de alvo os anéis deslizam até o novo; depois acompanham o alvo de perto
            switchT = Mathf.Min(1f, switchT + dt / 0.18f);
            point   = Vector3.Lerp(point, aim, switchT >= 1f ? 1f : 1f - Mathf.Exp(-22f * dt));
        }
        visibility = Mathf.MoveTowards(visibility, target != null ? 1f : 0f,
                                       dt / Mathf.Max(0.01f, target != null ? acquireTime * 0.5f : releaseTime));
        if (visibility <= 0f || cam == null)
        {
            if (root.gameObject.activeSelf)
                root.gameObject.SetActive(false);
            showing = false;
            return;
        }
        if (!root.gameObject.activeSelf)
            root.gameObject.SetActive(true);

        appearT    = Mathf.Min(1f, appearT + dt / Mathf.Max(0.01f, acquireTime));
        pulse      = Mathf.MoveTowards(pulse, 0f, dt * 3.5f);
        angleOuter += outerSpeed * dt;
        angleInner += innerSpeed * dt;
        spin       += spinSpeed * dt;

        // Fechando de longe ao travar (com um leve passo além do ponto final), abrindo ao soltar
        float closing = target != null ? Mathf.LerpUnclamped(2.4f, 1f, EaseOutBack(appearT)) : Mathf.Lerp(1.7f, 1f, visibility);
        float scale   = Mathf.Clamp(Vector3.Distance(cam.transform.position, point) / referenceDistance, 0.85f, 2.2f);
        float radiusScale = closing * scale * (1f + pulse * 0.18f);

        Vector3 toCam  = cam.transform.position - point;
        Vector3 center = point + toCam.normalized * Mathf.Min(towardCamera, toCam.magnitude * 0.5f);
        Vector3 right  = cam.transform.right;
        Vector3 up     = cam.transform.up;
        Vector3 facing = -cam.transform.forward;

        for (int i = 0; i < outer.Length; i++)
        {
            float a = (angleOuter + i * 360f / outer.Length) * Mathf.Deg2Rad;
            Vector3 radial = right * Mathf.Cos(a) + up * Mathf.Sin(a);
            float length   = outerLength * scale * (1f + pulse * 0.35f);
            // Base no anel, ponta apontando para o centro
            outer[i].SetPositionAndRotation(center + radial * (outerRadius * radiusScale),
                                            Quaternion.LookRotation(-radial, facing) * Quaternion.Euler(0f, 0f, spin + i * 47f));
            outer[i].localScale = Vector3.one * length;
        }
        for (int i = 0; i < inner.Length; i++)
        {
            float a = (angleInner + i * 360f / inner.Length) * Mathf.Deg2Rad;
            Vector3 radial  = right * Mathf.Cos(a) + up * Mathf.Sin(a);
            // Deitado ao longo do círculo, apontando no sentido do giro
            Vector3 tangent = Vector3.Cross(facing, radial) * Mathf.Sign(innerSpeed);
            float length    = innerLength * scale;
            Vector3 pos     = center + radial * (innerRadius * radiusScale) - tangent * (length * 0.5f);
            inner[i].SetPositionAndRotation(pos, Quaternion.LookRotation(tangent, radial) * Quaternion.Euler(0f, 0f, -spin * 1.5f + i * 60f));
            inner[i].localScale = Vector3.one * length;
        }

        Color c = Color.Lerp(color, pulseColor, Mathf.Clamp01(pulse * 1.2f + (1f - appearT) * 0.8f));
        c.a = visibility * color.a;
        block.SetColor(BaseColorId, c);
        foreach (Renderer r in renderers)
            r.SetPropertyBlock(block);
    }

    private static float EaseOutBack(float t)
    {
        const float s = 1.70158f;
        t -= 1f;
        return t * t * ((s + 1f) * t + s) + 1f;
    }

    public bool IsShowing => showing;
}
