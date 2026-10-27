using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// HUD montado por código (não precisa de prefab nem de Canvas na cena): controles,
// aviso de interação, arsenal (teclas 1 a 5), contador de combo, medidor de estilo, barras de vida
// dos bonecos, marcador do alvo da mira assistida e números de dano. A mira travada tem o seu
// próprio indicador 3D (LockOnIndicator): com ela ligada o marcador simples some.
public class CombatHUD : MonoBehaviour
{
    public PlayerCombat player;
    public bool showControls = true;
    [Tooltip("Tempo sem acertar até o contador de combo zerar")]
    public float comboResetTime = 2.5f;
    [Tooltip("Barras de vida só aparecem até esta distância")]
    public float healthBarRange = 30f;

    [Header("Estilo")]
    [Tooltip("Pontos para subir cada nível (D, C, B, A, S, SS, SSS)")]
    public float styleRankSize = 100f;
    [Tooltip("Pontos perdidos por segundo sem acertar (cresce com o nível)")]
    public float styleDecay = 18f;
    [Tooltip("Sem acertar por este tempo o estilo cai mais rápido")]
    public float styleIdleTime = 2.5f;

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
    private TargetLock targetLock;
    private Text arsenalLabel;
    private RectTransform styleRoot;
    private CanvasGroup styleGroup;
    private Text styleRank;
    private Text styleName;
    private RectTransform styleFill;
    private float style;
    private float stylePunch;
    private int lastRank = -1;
    private float lastStyleHit = -999f;
    private readonly List<string> recentAttacks = new List<string>();

    private static readonly string[] RankLetters = { "D", "C", "B", "A", "S", "SS", "SSS" };
    private static readonly string[] RankNames = { "Desajeitado", "Certeiro", "Brutal", "Arrasador", "Sanguinário", "Selvagem", "Supremo" };
    private static readonly Color[] RankColors =
    {
        new Color(0.7f, 0.75f, 0.8f), new Color(0.55f, 0.85f, 1f), new Color(0.5f, 1f, 0.6f), new Color(1f, 0.85f, 0.35f),
        new Color(1f, 0.55f, 0.2f), new Color(1f, 0.3f, 0.25f), new Color(1f, 0.25f, 0.6f)
    };
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
            player.HitLanded      += OnHitLanded;
            player.AttackStarted  += OnAttackStarted;
            player.WeaponChanged  += OnWeaponChanged;
            player.ArsenalChanged += RefreshArsenal;
            targetLock = player.GetComponent<TargetLock>();
            OnWeaponChanged(player.Weapon);
        }
    }

    private void OnDisable()
    {
        if (player != null)
        {
            player.HitLanded      -= OnHitLanded;
            player.AttackStarted  -= OnAttackStarted;
            player.WeaponChanged  -= OnWeaponChanged;
            player.ArsenalChanged -= RefreshArsenal;
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
            controls.text = "WASD  mover        Alt / Caps Lock  andar        Shift  disparar        Espaço  pular (2x no ar: pulo duplo)\n" +
                            "Clique  golpe leve        Botão direito  golpe pesado (no meio da sequência muda o finalizador)\n" +
                            "Q  lançador (sobe junto)        No ar: clique  combo aéreo,  botão direito  golpe de queda        Disparando + clique  investida\n" +
                            "E / botão do meio  travar mira        Tab / puxão do mouse  trocar alvo        Ctrl  esquivar\n" +
                            "F  pegar arma        G  largar        1-5  trocar arma        Scroll  zoom        Esc  soltar mouse        F2  modo da locomoção";
            Place(controls.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(28f, 24f), new Vector2(1200f, 150f));
        }

        weaponLabel = CreateText("Weapon", canvasRect, 30, TextAnchor.UpperLeft, Color.white);
        Place(weaponLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -24f), new Vector2(500f, 40f));
        arsenalLabel = CreateText("Arsenal", canvasRect, 22, TextAnchor.UpperLeft, Color.white);
        Place(arsenalLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -66f), new Vector2(900f, 34f));

        BuildStyleMeter();

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

    private void BuildStyleMeter()
    {
        var go = new GameObject("Style", typeof(RectTransform), typeof(CanvasGroup));
        styleRoot = go.GetComponent<RectTransform>();
        styleRoot.SetParent(canvasRect, false);
        Place(styleRoot, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -30f), new Vector2(320f, 170f));
        styleRoot.pivot = new Vector2(1f, 1f);
        styleGroup       = go.GetComponent<CanvasGroup>();
        styleGroup.alpha = 0f;

        styleRank = CreateText("Rank", styleRoot, 110, TextAnchor.UpperRight, Color.white);
        styleRank.fontStyle = FontStyle.BoldAndItalic;
        Place(styleRank.rectTransform, new Vector2(0f, 0.35f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero, true);
        styleName = CreateText("Name", styleRoot, 26, TextAnchor.MiddleRight, Color.white);
        styleName.fontStyle = FontStyle.Italic;
        Place(styleName.rectTransform, new Vector2(0f, 0.15f), new Vector2(1f, 0.35f), Vector2.zero, Vector2.zero, true);

        RectTransform bar = CreateImage("Bar", styleRoot, new Color(0f, 0f, 0f, 0.55f));
        Place(bar, new Vector2(0.25f, 0f), new Vector2(1f, 0.1f), Vector2.zero, Vector2.zero, true);
        styleFill = CreateImage("Fill", bar, Color.white);
        styleFill.anchorMin = Vector2.zero;
        styleFill.anchorMax = Vector2.one;
        styleFill.pivot     = new Vector2(0f, 0.5f);
        styleFill.offsetMin = new Vector2(2f, 2f);
        styleFill.offsetMax = new Vector2(-2f, -2f);
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

    private string currentAttackName;

    private void OnAttackStarted(AttackData attack, int index)
    {
        currentAttackName = attack != null ? attack.name : null;
    }

    private void OnHitLanded(DamageInfo info, Health target)
    {
        comboCount  = Time.unscaledTime - lastHitTime <= comboResetTime ? comboCount + 1 : 1;
        lastHitTime = Time.unscaledTime;
        comboPunch  = info.heavy ? 0.6f : 0.35f;
        comboNumber.text = comboCount.ToString();
        SpawnDamageNumber(info);
        AddStyle(info);
    }

    // Estilo: variar os golpes rende mais que repetir o mesmo; golpes no ar, lançadores, quedas e
    // finalizadores valem mais. Parado, o medidor esvazia.
    private void AddStyle(DamageInfo info)
    {
        string attack = currentAttackName ?? "";
        int repeats = 0;
        foreach (string recent in recentAttacks)
            if (recent == attack) repeats++;
        recentAttacks.Add(attack);
        if (recentAttacks.Count > 6)
            recentAttacks.RemoveAt(0);

        float points = 10f + info.amount * 0.6f;
        if (info.airborneHit) points *= 1.35f;
        if (info.reaction == HitReaction.Launch || info.reaction == HitReaction.Slam || info.reaction == HitReaction.Knockback)
            points *= 1.6f;
        else if (info.heavy)
            points *= 1.3f;
        points /= 1f + repeats * 0.7f;
        // O medidor para no topo do SSS; níveis altos esvaziam mais rápido (UpdateStyle)
        style        = Mathf.Min(style + points, styleRankSize * RankLetters.Length - 0.01f);
        lastStyleHit = Time.unscaledTime;
        stylePunch   = 0.25f;
    }

    private void OnWeaponChanged(MeleeWeapon weapon)
    {
        if (weaponLabel != null)
            weaponLabel.text = "Arma: " + (weapon != null ? weapon.DisplayName : "Punhos");
        RefreshArsenal();
    }

    private void RefreshArsenal()
    {
        if (arsenalLabel == null || player == null)
            return;
        var sb = new System.Text.StringBuilder();
        AppendSlot(sb, 1, "Punhos", player.Weapon == null);
        foreach (MeleeWeapon w in player.Arsenal)
            if (w != null)
                AppendSlot(sb, w.slot, w.DisplayName, w == player.Weapon);
        arsenalLabel.text = sb.ToString();
    }

    private static void AppendSlot(System.Text.StringBuilder sb, int slot, string label, bool active)
    {
        if (sb.Length > 0)
            sb.Append("    ");
        sb.Append(active ? "<color=#ffd75a>" : "<color=#ffffffaa>");
        sb.Append('[').Append(slot).Append("] ").Append(label).Append("</color>");
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
        UpdateStyle(dt);
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

    private void UpdateStyle(float dt)
    {
        int rank = Mathf.Clamp(Mathf.FloorToInt(style / styleRankSize), 0, RankLetters.Length - 1);
        float decay = styleDecay * (1f + rank * 0.35f) * (Time.unscaledTime - lastStyleHit > styleIdleTime ? 3f : 1f);
        style = Mathf.Max(0f, style - decay * dt);
        rank  = Mathf.Clamp(Mathf.FloorToInt(style / styleRankSize), 0, RankLetters.Length - 1);

        bool visible = style > 1f;
        styleGroup.alpha = Mathf.MoveTowards(styleGroup.alpha, visible ? 1f : 0f, dt * (visible ? 8f : 2f));
        if (rank != lastRank)
        {
            if (rank > lastRank)
                stylePunch = 0.6f;
            lastRank        = rank;
            styleRank.text  = RankLetters[rank];
            styleName.text  = RankNames[rank];
            styleRank.color = RankColors[rank];
            styleName.color = RankColors[rank];
            styleFill.GetComponent<Image>().color = RankColors[rank];
        }
        stylePunch = Mathf.MoveTowards(stylePunch, 0f, dt * 2.5f);
        styleRank.rectTransform.localScale = Vector3.one * (1f + stylePunch);
        styleFill.localScale = new Vector3(Mathf.Repeat(style, styleRankSize) / styleRankSize, 1f, 1f);
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
        if (targetLock != null && targetLock.IsLocked)
            target = null; // a mira travada tem o indicador de prismas
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
