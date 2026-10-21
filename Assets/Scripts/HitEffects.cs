using UnityEngine;

// Faíscas e clarão de impacto gerados por código (sem prefab): dois ParticleSystems
// reaproveitados que emitem partículas no ponto de cada acerto.
public class HitEffects : MonoBehaviour
{
    [Tooltip("Material de partícula aditivo (URP Particles/Unlit)")]
    public Material particleMaterial;
    public Color sparkColor = new Color(1f, 0.85f, 0.45f);
    public Color heavySparkColor = new Color(1f, 0.55f, 0.25f);
    public int sparkCount = 14;

    private static HitEffects instance;
    private ParticleSystem sparks;
    private ParticleSystem flash;

    public static void Spawn(Vector3 point, Vector3 direction, bool heavy)
    {
        if (instance != null)
            instance.Emit(point, direction, heavy);
    }

    private void Awake()
    {
        instance = this;
        Texture2D dot = CreateSoftDot(64);
        sparks = CreateSystem("Sparks", dot, true);
        flash  = CreateSystem("Flash", dot, false);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    private void Emit(Vector3 point, Vector3 direction, bool heavy)
    {
        var emit  = new ParticleSystem.EmitParams();
        int count = heavy ? sparkCount * 2 : sparkCount;
        Color color = heavy ? heavySparkColor : sparkColor;

        for (int i = 0; i < count; i++)
        {
            // Espalha para frente (direção do golpe) e um pouco para cima.
            Vector3 spread = (direction + Random.insideUnitSphere * 0.9f + Vector3.up * 0.35f).normalized;
            emit.position      = point;
            emit.velocity      = spread * Random.Range(4f, heavy ? 12f : 9f);
            emit.startColor    = color;
            emit.startSize     = Random.Range(0.04f, 0.09f);
            emit.startLifetime = Random.Range(0.12f, 0.3f);
            sparks.Emit(emit, 1);
        }

        var flashEmit = new ParticleSystem.EmitParams
        {
            position      = point,
            velocity      = Vector3.zero,
            startColor    = new Color(1f, 0.95f, 0.8f, 0.9f),
            startSize     = heavy ? 1.8f : 1.1f,
            startLifetime = 0.09f
        };
        flash.Emit(flashEmit, 1);
    }

    private ParticleSystem CreateSystem(string systemName, Texture2D texture, bool stretched)
    {
        var go = new GameObject(systemName);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = ps.main;
        main.loop            = false;
        main.playOnAwake     = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = stretched ? 1.5f : 0f;
        main.maxParticles    = 400;
        main.useUnscaledTime = true; // as faíscas voam durante o hit stop: o impacto "congela" mas o efeito não

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = false;
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;

        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                     new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color   = fade;

        ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size    = new ParticleSystem.MinMaxCurve(1f, stretched
            ? AnimationCurve.Linear(0f, 1f, 1f, 0.3f)
            : AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.2f));

        var psRenderer = go.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (stretched)
        {
            psRenderer.velocityScale = 0.035f;
            psRenderer.lengthScale   = 1.5f;
        }
        psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        psRenderer.receiveShadows    = false;
        if (particleMaterial != null)
        {
            var material = new Material(particleMaterial);
            material.SetTexture("_BaseMap", texture);
            psRenderer.sharedMaterial = material;
        }

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
