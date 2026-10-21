using UnityEngine;

// Sons de combate sintetizados por código (o projeto ainda não tem arquivos de áudio):
// "whoosh" quando o golpe sai, impacto quando acerta e um sopro na esquiva.
// Para usar sons de verdade, troque os AudioClips gerados em Awake por assets.
[RequireComponent(typeof(AudioSource))]
public class CombatAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.7f;
    public PlayerCombat combat;

    private const int SampleRate = 44100;

    private AudioSource source;
    private AudioClip swing;
    private AudioClip hit;
    private AudioClip heavyHit;
    private AudioClip dodge;

    private void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake  = false;
        source.spatialBlend = 0f;

        swing    = CreateWhoosh("Swing", 0.22f, 700f, 3200f, 1);
        dodge    = CreateWhoosh("Dodge", 0.38f, 350f, 1400f, 2);
        hit      = CreateImpact("Hit", 0.16f, 110f, 3);
        heavyHit = CreateImpact("HeavyHit", 0.3f, 70f, 4);

        if (combat == null)
            combat = GetComponent<PlayerCombat>();
    }

    private void OnEnable()
    {
        if (combat == null)
            combat = GetComponent<PlayerCombat>();
        if (combat == null)
            return;
        combat.SwingStarted += OnSwing;
        combat.HitLanded    += OnHit;
        combat.DodgeStarted += OnDodge;
    }

    private void OnDisable()
    {
        if (combat == null)
            return;
        combat.SwingStarted -= OnSwing;
        combat.HitLanded    -= OnHit;
        combat.DodgeStarted -= OnDodge;
    }

    private void OnSwing() => Play(swing, 0.45f, Random.Range(0.9f, 1.15f));
    private void OnDodge() => Play(dodge, 0.35f, Random.Range(0.95f, 1.05f));
    private void OnHit(DamageInfo info, Health target) => Play(info.heavy ? heavyHit : hit, 1f, Random.Range(0.92f, 1.08f));

    private void Play(AudioClip clip, float clipVolume, float pitch)
    {
        if (clip == null || volume <= 0f)
            return;
        source.pitch = pitch;
        source.PlayOneShot(clip, clipVolume * volume);
    }

    // Ruído branco filtrado por um passa-baixa cuja frequência sobe e desce: som de lâmina cortando o ar.
    private static AudioClip CreateWhoosh(string clipName, float duration, float lowCutoff, float highCutoff, int seed)
    {
        int count   = Mathf.CeilToInt(duration * SampleRate);
        var data    = new float[count];
        var random  = new System.Random(seed);
        float lp1 = 0f, lp2 = 0f;

        for (int i = 0; i < count; i++)
        {
            float t        = (float)i / count;
            float envelope = Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.55f));
            envelope      *= envelope;
            float cutoff   = Mathf.Lerp(lowCutoff, highCutoff, Mathf.Sin(Mathf.PI * t));
            float alpha    = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / SampleRate);
            float noise    = (float)(random.NextDouble() * 2.0 - 1.0);
            lp1 += alpha * (noise - lp1);
            lp2 += alpha * (lp1 - lp2);
            data[i] = lp2 * envelope;
        }
        return CreateClip(clipName, data, 0.8f);
    }

    // Senoide grave com queda de frequência (o "corpo" do impacto) + estalo curto de ruído.
    private static AudioClip CreateImpact(string clipName, float duration, float frequency, int seed)
    {
        int count  = Mathf.CeilToInt(duration * SampleRate);
        var data   = new float[count];
        var random = new System.Random(seed);
        float phase = 0f, lp = 0f;
        float clickAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * 2500f / SampleRate);

        for (int i = 0; i < count; i++)
        {
            float t     = (float)i / SampleRate;
            float freq  = frequency * (1f + 1.5f * Mathf.Exp(-t * 40f));
            phase      += 2f * Mathf.PI * freq / SampleRate;
            float body  = Mathf.Sin(phase) * Mathf.Exp(-t * 16f);
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            lp         += clickAlpha * (noise - lp);
            float click = lp * Mathf.Exp(-t * 70f);
            data[i] = body * 0.9f + click * 1.4f;
        }
        return CreateClip(clipName, data, 0.9f);
    }

    private static AudioClip CreateClip(string clipName, float[] data, float peak)
    {
        float max = 0.0001f;
        foreach (float sample in data)
            max = Mathf.Max(max, Mathf.Abs(sample));
        float gain = peak / max;
        for (int i = 0; i < data.Length; i++)
            data[i] *= gain;

        AudioClip clip = AudioClip.Create(clipName, data.Length, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
