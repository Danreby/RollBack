using UnityEngine;

// Controla a escala de tempo do jogo:
//   - hit stop: "congela" o jogo por alguns milissegundos quando um golpe acerta (peso no impacto);
//   - câmera lenta: o tempo desacelera por um instante (esquiva perfeita) e volta suavemente.
// As duas camadas se combinam (vale a mais lenta), então um acerto durante a câmera lenta não a cancela.
public class HitStop : MonoBehaviour
{
    private static HitStop instance;

    private float endTime;   // em tempo real (não escalado)
    private bool active;
    private float stopScale = 1f;

    private float slowEndTime;
    private float slowScale = 1f;
    private float slowRecovery = 0.25f;
    private float slowCurrent = 1f;

    public static bool IsActive => instance != null && instance.active;
    public static bool IsSlowMotion => instance != null && instance.slowCurrent < 0.999f;
    // 0 = tempo normal, 1 = câmera lenta no máximo (usado pelos efeitos de tela)
    public static float SlowMotionAmount => instance == null || instance.slowScale >= 1f
        ? 0f
        : Mathf.Clamp01((1f - instance.slowCurrent) / (1f - instance.slowScale));

    public static void Request(float duration, float timeScale = 0.03f)
    {
        if (duration <= 0f || !Application.isPlaying)
            return;
        Instance.Begin(duration, timeScale);
    }

    // Câmera lenta por 'duration' segundos de tempo real, voltando ao normal em 'recovery' segundos.
    public static void RequestSlowMotion(float duration, float timeScale = 0.3f, float recovery = 0.3f)
    {
        if (duration <= 0f || !Application.isPlaying)
            return;
        HitStop hs = Instance;
        hs.slowEndTime  = Mathf.Max(hs.slowEndTime, Time.unscaledTime + duration);
        hs.slowScale    = Mathf.Min(hs.slowCurrent < 0.999f ? hs.slowScale : 1f, timeScale);
        hs.slowRecovery = Mathf.Max(0.01f, recovery);
        hs.slowCurrent  = hs.slowScale;
        hs.Apply();
    }

    private static HitStop Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("HitStop");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<HitStop>();
            }
            return instance;
        }
    }

    private void Begin(float duration, float timeScale)
    {
        endTime   = Mathf.Max(endTime, Time.unscaledTime + duration);
        stopScale = active ? Mathf.Min(stopScale, timeScale) : timeScale;
        active    = true;
        Apply();
    }

    private void Update()
    {
        // Update roda todo frame mesmo com timeScale quase zero.
        if (active && Time.unscaledTime >= endTime)
        {
            active    = false;
            stopScale = 1f;
        }
        if (slowCurrent < 1f && Time.unscaledTime >= slowEndTime)
            slowCurrent = Mathf.MoveTowards(slowCurrent, 1f, Time.unscaledDeltaTime * (1f - slowScale) / slowRecovery);
        if (slowCurrent >= 1f)
            slowScale = 1f;
        Apply();
    }

    private void Apply()
    {
        Time.timeScale = Mathf.Min(active ? stopScale : 1f, slowCurrent);
    }

    private void OnDisable()
    {
        active      = false;
        stopScale   = 1f;
        slowCurrent = 1f;
        slowScale   = 1f;
        Time.timeScale = 1f;
    }
}
