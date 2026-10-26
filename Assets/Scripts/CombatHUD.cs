using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// HUD montado por código (não precisa de prefab nem de Canvas na cena): controles,
// aviso de interação, arma atual, contador de combo, barras de vida dos bonecos,
// marcador do alvo da mira assistida e números de dano.
public class CombatHUD : MonoBehaviour
{
    public PlayerCombat player;
    public bool showControls = true;
    [Tooltip("Tempo sem acertar até o contador de combo zerar")]
    public float comboResetTime = 2.5f;
    [Tooltip("Barras de vida só aparecem até esta distância")]
    public float healthBarRange = 30f;

    private class HealthBar
    {
        public TrainingDummy dummy;
        public RectTransform root;
        public RectTransform fill;
        public RectTransform trail;
        public float trailValue = 1f;
    }

    private class DamageNumber
    {
        public Text text;
        public Vector3 worldPosition;
        public float age;
        public float lifetime;
    }

    private static CombatHUD instance;
    private static object promptOwner;
    private static string promptText;

    private RectTransform canvasRect;
    private Font font;
    private Text promptLabel;
    private Text weaponLabel;
    private Text comboNumber;
    private RectTransform comboRoot;
    private CanvasGroup comboGroup;
    private RectTransform targetMarker;
    private float comboPunch;
    private int comboCount;
    private float lastHitTime = -999f;
    private readonly List<HealthBar> bars = new List<HealthBar>();
    private readonly List<DamageNumber> numbers = new List<DamageNumber>();

    // Chamado por quem quer mostrar um aviso ("[E] Pegar espada"); texto nulo esconde.
    public static void SetPrompt(object owner, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (promptOwner == owner)
            {
                promptOwner = null;
                promptText  = null;
            }
            return;
        }
        promptOwner = owner;
        promptText  = text;
    }

    private void Awake()
    {
        instance    = this;
        promptOwner = null; // estáticos sobrevivem entre Plays com "domain reload" desligado
        promptText  = null;
        font        = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        BuildCanvas();
    }

    private void OnEnable()
    {
        if (player == null)
            player = FindFirstObjectByType<PlayerCombat>();
        if (player != null)
        {
            player.HitLanded     += OnHitLanded;
            player.WeaponChanged += OnWeaponChanged;
            OnWeaponChanged(player.IsArmed);
        }
    }

    private void OnDisable()
    {
        if (player != null)
        {
            player.HitLanded     -= OnHitLanded;
            player.WeaponChanged -= OnWeaponChanged;
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    // ---- Montagem ----

    private void BuildCanvas()
    {
        var canvasGo = new GameObject("HUD Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        canvasRect = canvasGo.GetComponent<RectTransform>();

        if (showControls)
        {
            Text controls = CreateText("Controls", canvasRect, 20, TextAnchor.LowerLeft, new Color(1f, 1f, 1f, 0.85f));
            controls.text = "WASD  mover        Shift  correr        Espaço  pular\n" +
                            "Clique  atacar (no ritmo, encadeia o combo)        Correndo + clique  investida\n" +
                            "Ctrl / Botão direito  esquivar (invencível no começo)\n" +
                            "E  pegar espada        G  largar        Scroll  zoom        Esc  soltar mouse";
            Place(controls.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(28f, 24f), new Vector2(900f, 120f));
        }

        weaponLabel = CreateText("Weapon", canvasRect, 26, TextAnchor.UpperLeft, Color.white);
        Place(weaponLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -24f), new Vector2(500f, 40f));

        promptLabel = CreateText("Prompt", canvasRect, 32, TextAnchor.MiddleCenter, new Color(1f, 0.95f, 0.7f));
        Place(promptLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 200f), new Vector2(700f, 50f));
        promptLabel.enabled = false;

        var comboGo = new GameObject("Combo", typeof(RectTransform), typeof(CanvasGroup));
        comboRoot   = comboGo.GetComponent<RectTransform>();
        comboRoot.SetParent(canvasRect, false);
        Place(comboRoot, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-60f, 80f), new Vector2(260f, 160f));
        comboGroup       = comboGo.GetComponent<CanvasGroup>();
        comboGroup.alpha = 0f;
        comboNumber      = CreateText("Count", comboRoot, 96, TextAnchor.LowerRight, new Color(1f, 0.85f, 0.35f));
        Place(comboNumber.rectTransform, new Vector2(0f, 0.3f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero, true);
        Text hits = CreateText("Label", comboRoot, 30, TextAnchor.UpperRight, Color.white);
        hits.text = "HITS";
        Place(hits.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.3f), Vector2.zero, Vector2.zero, true);

        targetMarker = CreateImage("Target", canvasRect, new Color(1f, 0.85f, 0.35f, 0.9f));
        targetMarker.sizeDelta        = new Vector2(16f, 16f);
        targetMarker.localEulerAngles = new Vector3(0f, 0f, 45f);
        targetMarker.gameObject.SetActive(false);
    }

    private Text CreateText(string objectName, RectTransform parent, int size, TextAnchor anchor, Color color)
    {
        var go   = new GameObject(objectName, typeof(RectTransform), typeof(Text), typeof(Outline));
        var text = go.GetComponent<Text>();
        go.transform.SetParent(parent, false);
        text.font               = font;
        text.fontSize           = size;
        text.alignment          = anchor;
        text.color              = color;
        text.raycastTarget      = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow   = VerticalWrapMode.Overflow;
        go.GetComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.75f);
        return text;
    }

    private static RectTransform CreateImage(string objectName, RectTransform parent, Color color)
    {
        var go = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color         = color;
        image.raycastTarget = false;
        return go.GetComponent<RectTransform>();
    }

    private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size, bool stretch = false)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        if (stretch)
        {
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return;
        }
        rect.pivot            = anchorMin;
        rect.anchoredPosition = position;
        rect.sizeDelta        = size;
    }

    private HealthBar CreateBar(TrainingDummy dummy)
    {
        var bar = new HealthBar { dummy = dummy };
        bar.root = CreateImage("HealthBar", canvasRect, new Color(0f, 0f, 0f, 0.6f));
        bar.root.sizeDelta = new Vector2(120f, 12f);
        bar.trail = CreateImage("Trail", bar.root, new Color(1f, 1f, 1f, 0.85f));
        bar.fill  = CreateImage("Fill", bar.root, new Color(0.85f, 0.2f, 0.15f));
        foreach (RectTransform part in new[] { bar.trail, bar.fill })
        {
            part.anchorMin = new Vector2(0f, 0f);
            part.anchorMax = new Vector2(1f, 1f);
            part.pivot     = new Vector2(0f, 0.5f);
            part.offsetMin = new Vector2(2f, 2f);
            part.offsetMax = new Vector2(-2f, -2f);
        }
        bars.Add(bar);
        return bar;
    }

    // ---- Eventos ----

    private void OnHitLanded(DamageInfo info, Health target)
    {
        comboCount  = Time.unscaledTime - lastHitTime <= comboResetTime ? comboCount + 1 : 1;
        lastHitTime = Time.unscaledTime;
        comboPunch  = info.heavy ? 0.6f : 0.35f;
        comboNumber.text = comboCount.ToString();
        SpawnDamageNumber(info);
    }

    private void OnWeaponChanged(bool armed)
    {
        if (weaponLabel != null)
            weaponLabel.text = armed ? "Arma: Espada" : "Arma: Punhos";
    }

    private void SpawnDamageNumber(DamageInfo info)
    {
        DamageNumber number = numbers.Find(n => !n.text.gameObject.activeSelf);
        if (number == null)
        {
            number = new DamageNumber { text = CreateText("Damage", canvasRect, 34, TextAnchor.MiddleCenter, Color.white) };
            number.text.rectTransform.sizeDelta = new Vector2(160f, 50f);
            numbers.Add(number);
        }
        number.text.gameObject.SetActive(true);
        number.text.text     = Mathf.RoundToInt(info.amount).ToString();
        number.text.fontSize = info.heavy ? 48 : 34;
        number.text.color    = info.heavy ? new Color(1f, 0.6f, 0.2f) : Color.white;
        number.worldPosition = info.point + Random.insideUnitSphere * 0.25f;
        number.age           = 0f;
        number.lifetime      = info.heavy ? 1.1f : 0.8f;
    }

    // ---- Atualização ----

    private void LateUpdate()
    {
        float dt   = Time.unscaledDeltaTime;
        Camera cam = Camera.main;

        promptLabel.enabled = !string.IsNullOrEmpty(promptText);
        if (promptLabel.enabled)
            promptLabel.text = promptText;

        UpdateCombo(dt);
        if (cam == null)
            return;
        UpdateHealthBars(cam, dt);
        UpdateTargetMarker(cam);
        UpdateDamageNumbers(cam, dt);
    }

    private void UpdateCombo(float dt)
    {
        bool visible     = comboCount > 1 && Time.unscaledTime - lastHitTime <= comboResetTime;
        comboGroup.alpha = Mathf.MoveTowards(comboGroup.alpha, visible ? 1f : 0f, dt * (visible ? 10f : 3f));
        comboPunch       = Mathf.MoveTowards(comboPunch, 0f, dt * 3f);
        comboRoot.localScale = Vector3.one * (1f + comboPunch);
        if (!visible && comboGroup.alpha <= 0f)
            comboCount = 0;
    }

    private void UpdateHealthBars(Camera cam, float dt)
    {
        foreach (TrainingDummy dummy in TrainingDummy.All)
            if (!bars.Exists(b => b.dummy == dummy))
                CreateBar(dummy);

        foreach (HealthBar bar in bars)
        {
            bool alive = bar.dummy != null && bar.dummy.isActiveAndEnabled;
            Vector2 local = Vector2.zero;
            bool onScreen = alive && WorldToCanvas(cam, bar.dummy.HeadPosition, out local)
                            && Vector3.Distance(cam.transform.position, bar.dummy.transform.position) <= healthBarRange;
            bar.root.gameObject.SetActive(onScreen);
            if (!onScreen)
                continue;

            bar.root.anchoredPosition = local;
            float value = bar.dummy.Health.Normalized;
            // A faixa branca "alcança" a vida atual com atraso: dá para ver quanto o golpe tirou.
            bar.trailValue = value > bar.trailValue ? value : Mathf.MoveTowards(bar.trailValue, value, dt * 0.6f);
            bar.fill.localScale  = new Vector3(value, 1f, 1f);
            bar.trail.localScale = new Vector3(bar.trailValue, 1f, 1f);
        }
    }

    private void UpdateTargetMarker(Camera cam)
    {
        Health target = player != null ? player.PreviewTarget : null;
        Vector2 local = Vector2.zero;
        bool show = target != null && WorldToCanvas(cam, target.transform.position + Vector3.up * 3.1f, out local);
        targetMarker.gameObject.SetActive(show);
        if (show)
            targetMarker.anchoredPosition = local + new Vector2(0f, Mathf.Sin(Time.unscaledTime * 6f) * 4f);
    }

    private void UpdateDamageNumbers(Camera cam, float dt)
    {
        foreach (DamageNumber number in numbers)
        {
            if (!number.text.gameObject.activeSelf)
                continue;
            number.age += dt;
            float t = number.age / number.lifetime;
            if (t >= 1f || !WorldToCanvas(cam, number.worldPosition + Vector3.up * (1.2f * t), out Vector2 local))
            {
                number.text.gameObject.SetActive(false);
                continue;
            }
            number.text.rectTransform.anchoredPosition = local;
            Color c = number.text.color;
            c.a = 1f - t * t;
            number.text.color = c;
            number.text.rectTransform.localScale = Vector3.one * (1f + 0.4f * Mathf.Max(0f, 1f - t * 6f));
        }
    }

    private bool WorldToCanvas(Camera cam, Vector3 world, out Vector2 local)
    {
        Vector3 screen = cam.WorldToScreenPoint(world);
        local = Vector2.zero;
        if (screen.z <= 0f)
            return false;
        // Elementos criados com âncora no centro do Canvas
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, null, out local);
    }
}
