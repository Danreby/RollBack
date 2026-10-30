using UnityEngine;

// Boneco de sparring: um boneco de treino que também ataca, para testar defesa e esquiva.
//
// Quando o player chega perto, o boneco avisa o golpe (brilha em vermelho e gira para o player),
// depois ataca com uma animação da Human Melee Animations. Se o player estiver no alcance na hora
// do golpe, leva dano (reação, empurrão; com a vida zerada ele cai e levanta). Se estiver no meio
// da esquiva, com os frames de invencibilidade, é uma ESQUIVA PERFEITA: o tempo fica lento por um
// instante (ideia do "SlowTime" do RPG Character Mecanim Animation Pack). Apanhar interrompe o
// ataque do boneco, então dá para "ganhar a troca" golpeando primeiro.
[RequireComponent(typeof(TrainingDummy))]
public class SparringAI : MonoBehaviour
{
    [Tooltip("Desligado, vira um boneco de treino comum")]
    public bool aggressive = true;
    [Tooltip("Começa um ataque quando o player está mais perto que isso (centro a centro)")]
    public float attackRange = 3.2f;
    [Tooltip("Espera (s) entre um ataque e outro (sorteado entre x e y)")]
    public Vector2 cooldown = new Vector2(1.6f, 3f);

    [Header("Aviso")]
    [Tooltip("Tempo (s) brilhando e girando para o player antes do golpe sair")]
    public float telegraphTime = 0.6f;
    public float telegraphTurnSpeed = 400f;
    public Color telegraphColor = new Color(1f, 0.15f, 0.05f);
    public float telegraphIntensity = 3f;

    [Header("Golpe")]
    public string attackState = "Attack";
    public AnimationClip attackClip;
    public float attackSpeed = 1f;
    [Tooltip("Janela do golpe, em tempo normalizado da animação")]
    public Vector2 hitWindow = new Vector2(0.22f, 0.34f);
    public float damage = 15f;
    public float knockback = 0.8f;
    [Tooltip("Alcance do golpe na janela (centro a centro)")]
    public float hitRange = 3.4f;
    [Range(0f, 180f)] public float hitAngle = 65f;
    [Tooltip("Estado de guarda entre os ataques")]
    public string guardState = "Idle";

    private enum Phase { Idle, Telegraph, Attack }

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private TrainingDummy dummy;
    private Health health;
    private Animator animator;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private PlayerCombat player;
    private Health playerHealth;
    private Phase phase;
    private float phaseTime;
    private float nextAttackTime;
    private bool resolved;

    public bool IsTelegraphing => phase == Phase.Telegraph;
    public bool IsAttacking => phase == Phase.Attack;

    private void Awake()
    {
        dummy     = GetComponent<TrainingDummy>();
        health    = GetComponent<Health>();
        animator  = GetComponent<Animator>();
        renderers = GetComponentsInChildren<Renderer>();
        block     = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        health.Damaged += OnInterrupted;
        health.Died    += OnInterrupted;
    }

    private void OnDisable()
    {
        health.Damaged -= OnInterrupted;
        health.Died    -= OnInterrupted;
        ClearGlow();
    }

    private void Start()
    {
        player = FindFirstObjectByType<PlayerCombat>();
        if (player != null)
            playerHealth = player.GetComponent<Health>();
        nextAttackTime = Time.time + Random.Range(cooldown.x, cooldown.y);
        dummy.PlayState(guardState, 0.2f);
    }

    // Apanhou (ou caiu): o ataque é cancelado e o boneco espera antes de tentar de novo
    private void OnInterrupted(DamageInfo info)
    {
        if (phase != Phase.Idle)
            ClearGlow();
        if (animator != null)
            animator.speed = 1f;
        phase          = Phase.Idle;
        dummy.facePlayer = true;
        nextAttackTime = Time.time + Random.Range(cooldown.x, cooldown.y);
    }

    private void Update()
    {
        if (player == null || playerHealth == null)
            return;

        switch (phase)
        {
            case Phase.Idle:
                if (aggressive && CanAttack())
                {
                    phase     = Phase.Telegraph;
                    phaseTime = 0f;
                    dummy.facePlayer = false; // o giro do aviso é feito aqui, mais rápido
                }
                break;
            case Phase.Telegraph:
                UpdateTelegraph(Time.deltaTime);
                break;
            case Phase.Attack:
                UpdateAttack(Time.deltaTime);
                break;
        }
    }

    private bool CanAttack()
    {
        if (Time.time < nextAttackTime || !dummy.IsStanding || dummy.IsStunned || playerHealth.IsDead)
            return false;
        Vector3 toPlayer = player.transform.position - transform.position;
        return Mathf.Abs(toPlayer.y) < 1.5f && new Vector2(toPlayer.x, toPlayer.z).magnitude <= attackRange;
    }

    private void UpdateTelegraph(float dt)
    {
        phaseTime += dt;
        FacePlayer(telegraphTurnSpeed, dt);
        // Brilho pulsando cada vez mais rápido: "vai bater!"
        float t = phaseTime / Mathf.Max(0.01f, telegraphTime);
        float pulse = 0.5f + 0.5f * Mathf.Sin(phaseTime * Mathf.Lerp(10f, 30f, t));
        SetGlow(telegraphColor * (telegraphIntensity * Mathf.Lerp(0.3f, 1f, t) * (0.4f + 0.6f * pulse)));

        if (phaseTime >= telegraphTime)
        {
            ClearGlow();
            phase     = Phase.Attack;
            phaseTime = 0f;
            resolved  = false;
            if (animator != null)
                animator.speed = attackSpeed;
            dummy.PlayState(attackState, 0.05f);
        }
    }

    private void UpdateAttack(float dt)
    {
        phaseTime += dt;
        float duration = (attackClip != null ? attackClip.length : 1f) / Mathf.Max(0.05f, attackSpeed);
        float n = phaseTime / duration;

        if (!resolved && n >= hitWindow.x)
        {
            if (TryHitPlayer())
                resolved = true;
            else if (n > hitWindow.y)
                resolved = true;
        }

        if (n >= 0.95f)
        {
            if (animator != null)
                animator.speed = 1f;
            phase            = Phase.Idle;
            dummy.facePlayer = true;
            nextAttackTime   = Time.time + Random.Range(cooldown.x, cooldown.y);
            dummy.PlayState(guardState, 0.25f);
        }
    }

    // Devolve true quando o golpe foi resolvido (acertou ou o player esquivou de forma perfeita)
    private bool TryHitPlayer()
    {
        Vector3 toPlayer = player.transform.position - transform.position;
        float height = toPlayer.y;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;
        if (distance > hitRange || Mathf.Abs(height) > 1.6f || Vector3.Angle(transform.forward, toPlayer) > hitAngle)
            return false;

        if (playerHealth.Invulnerable)
        {
            // Pegaria em cheio, mas o player está nos frames de invencibilidade da esquiva
            if (player.IsDodging)
                player.NotifyPerfectDodge();
            return true;
        }

        var info = new DamageInfo
        {
            amount    = damage,
            point     = player.transform.position + Vector3.up * 1.4f,
            direction = distance > 1e-3f ? toPlayer / distance : transform.forward,
            knockback = knockback,
            source    = gameObject,
        };
        if (playerHealth.TakeDamage(info))
            HitEffects.Spawn(info.point - info.direction * 0.3f, info.direction, false);
        return true;
    }

    private void FacePlayer(float degreesPerSecond, float dt)
    {
        Vector3 toPlayer = player.transform.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toPlayer), degreesPerSecond * dt);
    }

    private void SetGlow(Color emission)
    {
        foreach (Renderer r in renderers)
        {
            if (r == null)
                continue;
            r.GetPropertyBlock(block);
            block.SetColor(EmissionColorId, emission);
            r.SetPropertyBlock(block);
        }
    }

    private void ClearGlow()
    {
        if (renderers == null)
            return;
        foreach (Renderer r in renderers)
            if (r != null)
                r.SetPropertyBlock(null);
    }
}
