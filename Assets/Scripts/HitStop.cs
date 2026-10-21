using UnityEngine;

// "Congela" o jogo por alguns milissegundos quando um golpe acerta.
// É um dos truques mais baratos para dar peso ao impacto.
public class HitStop : MonoBehaviour
{
    private static HitStop instance;

    private float endTime;   // em tempo real (não escalado)
    private bool active;

    public static bool IsActive => instance != null && instance.active;

    public static void Request(float duration, float timeScale = 0.03f)
    {
        if (duration <= 0f || !Application.isPlaying)
            return;

        if (instance == null)
        {
            var go = new GameObject("HitStop");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<HitStop>();
        }
        instance.Begin(duration, timeScale);
    }

    private void Begin(float duration, float timeScale)
    {
        endTime        = Mathf.Max(endTime, Time.unscaledTime + duration);
        Time.timeScale = active ? Mathf.Min(Time.timeScale, timeScale) : timeScale;
        active         = true;
    }

    private void Update()
    {
        // Update roda todo frame mesmo com timeScale quase zero.
        if (active && Time.unscaledTime >= endTime)
            End();
    }

    private void End()
    {
        active         = false;
        Time.timeScale = 1f;
    }

    private void OnDisable()
    {
        if (active)
            End();
    }
}
