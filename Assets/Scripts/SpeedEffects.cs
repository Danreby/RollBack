using UnityEngine;
using UnityEngine.Rendering;

// Efeitos de "sentir a velocidade", na câmera:
//   - linhas de vento passando ao redor da tela na disparada (nunca no centro, onde está o personagem);
//   - volume de pós-processamento da disparada (motion blur, aberração cromática, vinheta e um leve
//     "túnel" de distorção da lente), com peso proporcional à velocidade;
//   - poeira levantada a cada passada na disparada;
//   - volume da câmera lenta (esquiva perfeita): dessatura e fecha a vinheta enquanto o tempo está lento.
// A intensidade vem de ThirdPersonCamera.SpeedFactor (0 correndo, 1 na disparada máxima).
[RequireComponent(typeof(ThirdPersonCamera))]
public class SpeedEffects : MonoBehaviour
{
    [Tooltip("Material de partícula aditivo (URP Particles/Unlit) para as linhas de vento")]
    public Material streakMaterial;
    [Tooltip("Material de partícula com transparência normal para a poeira")]
    public Material dustMaterial;
    [Tooltip("Volume global da disparada (perfil com motion blur, aberração, vinheta)")]
    public Volume sprintVolume;
    [Tooltip("Volume global da câmera lenta")]
    public Volume slowMotionVolume;

    [Header("Linhas de vento")]
    public float streaksPerSecond = 90f;
    [Tooltip("Distância à frente da câmera onde as linhas nascem (m)")]
    public Vector2 streakDistance = new Vector2(5f, 13f);
    [Tooltip("Raio (m) do anel onde as linhas nascem: o centro da tela fica livre")]
    public Vector2 streakRadius = new Vector2(2.6f, 5.5f);
    public Color streakColor = new Color(1f, 1f, 1f, 0.35f);

    [Header("Poeira")]
    public Color dustColor = new Color(0.78f, 0.74f, 0.68f, 0.55f);
    public int dustPerStep = 6;

    private ThirdPersonCamera cam;
    private ThirdPersonController player;
    private AnimationEventSink steps;
    private ParticleSystem streaks;
    private ParticleSystem dust;
    private float streakAccumulator;
    private float sprintWeight;

    private void Start()
    {
        cam = GetComponent<ThirdPersonCamera>();
        if (cam.target != null)
        {
            player = cam.target.GetComponent<ThirdPersonController>();
            steps  = cam.target.GetComponent<AnimationEventSink>();
            if (steps != null)
                steps.Footstep += OnFootstep;
        }
        Texture2D dot = CreateSoftDot(32);
        streaks = CreateSystem("Speed Lines", streakMaterial, dot, true);
        dust    = CreateSystem("Sprint Dust", dustMaterial, dot, false);
        if (sprintVolume != null)
            sprintVolume.weight = 0f;
        if (slowMotionVolume != null)
            slowMotionVolume.weight = 0f;
    }

    private void OnDestroy()
    {
        if (steps != null)
            steps.Footstep -= OnFootstep;
    }

    private void LateUpdate()
    {
        float dt     = Time.unscaledDeltaTime;
        float factor = cam.SpeedFactor;

        sprintWeight = Mathf.Lerp(sprintWeight, factor, 1f - Mathf.Exp(-6f * dt));
        if (sprintVolume != null)
            sprintVolume.weight = sprintWeight;
        if (slowMotionVolume != null)
            slowMotionVolume.weight = Mathf.MoveTowards(slowMotionVolume.weight, HitStop.SlowMotionAmount, dt * 4f);

        EmitStreaks(factor, Time.deltaTime);
    }

    private void EmitStreaks(float factor, float dt)
    {
        if (streaks == null || player == null || factor < 0.05f || dt <= 0f)
            return;

        streakAccumulator += streaksPerSecond * factor * factor * dt;
        Vector3 velocity = player.PlanarVelocity;
        var emit = new ParticleSystem.EmitParams();
        Transform t = transform;
        while (streakAccumulator >= 1f)
        {
            streakAccumulator -= 1f;
            float angle  = Random.value * Mathf.PI * 2f;
            float radius = Random.Range(streakRadius.x, streakRadius.y);
            Vector3 position = t.position + t.forward * Random.Range(streakDistance.x, streakDistance.y)
                               + t.right * (Mathf.Cos(angle) * radius) + t.up * (Mathf.Sin(angle) * radius * 0.6f);
            emit.position      = position;
            // Passam pela câmera: velocidade do personagem invertida + um empurrão na direção da câmera
            emit.velocity      = -velocity * 1.3f - t.forward * 10f;
            emit.startColor    = streakColor;
            emit.startSize     = Random.Range(0.025f, 0.05f);
            emit.startLifetime = Random.Range(0.25f, 0.45f);
            streaks.Emit(emit, 1);
        }
    }

    // Poeira na disparada: uma nuvem baixa atrás do personagem a cada passo
    private void OnFootstep()
    {
        if (dust == null || player == null || cam.SpeedFactor < 0.3f || !player.IsGrounded)
            return;
        var emit = new ParticleSystem.EmitParams();
        Vector3 back = -player.PlanarVelocity.normalized;
        for (int i = 0; i < dustPerStep; i++)
        {
            emit.position      = player.transform.position + back * 0.3f + Random.insideUnitSphere * 0.25f + Vector3.up * 0.1f;
            emit.velocity      = (back * Random.Range(0.5f, 2f) + Vector3.up * Random.Range(0.4f, 1.2f) + Random.insideUnitSphere * 0.6f);
            emit.startColor    = dustColor;
            emit.startSize     = Random.Range(0.5f, 0.9f) * cam.SpeedFactor;
            emit.startLifetime = Random.Range(0.5f, 0.9f);
            dust.Emit(emit, 1);
        }
    }

    private ParticleSystem CreateSystem(string systemName, Material material, Texture2D texture, bool stretched)
    {
        if (material == null)
            return null;
        var go = new GameObject(systemName);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = ps.main;
        main.loop            = false;
        main.playOnAwake     = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles    = 500;
        main.gravityModifier = stretched ? 0f : -0.05f;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = false;
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;

        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                     stretched
                         ? new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) }
                         : new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        col.color   = fade;

        if (!stretched)
        {
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size    = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 1.6f));
            ParticleSystem.VelocityOverLifetimeModule drag = ps.velocityOverLifetime;
            drag.enabled = false;
            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag    = 2f;
        }

        var psRenderer = go.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (stretched)
        {
            psRenderer.velocityScale = 0.06f;
            psRenderer.lengthScale   = 2f;
        }
        psRenderer.shadowCastingMode = ShadowCastingMode.Off;
        psRenderer.receiveShadows    = false;
        var instance = new Material(material);
        instance.SetTexture("_BaseMap", texture);
        psRenderer.sharedMaterial = instance;
        ps.Play();
        return ps;
    }

    private static Texture2D CreateSoftDot(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                float a = Mathf.Clamp01(1f - d);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        texture.Apply();
        return texture;
    }
}
