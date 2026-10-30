using System;
using System.Collections.Generic;
using UnityEngine;

// Combate do player no estilo hack'n slash.
//
// Cada arma traz um WeaponMoveset (os punhos usam unarmedMoveset):
//   - clique esquerdo: sequência leve; clique direito: sequência pesada. Trocar de sequência no
//     meio continua do mesmo ponto na outra (L, L, P sai o 3º pesado), então o mesmo par de botões
//     gera finalizadores diferentes;
//   - Q: lançador (joga o alvo para o alto e sobe junto; emende o combo aéreo no ar);
//   - no ar: clique esquerdo = combo aéreo (a gravidade fica fraca durante os golpes), clique
//     direito = golpe de queda (mergulha e solta uma onda de choque ao tocar o chão);
//   - clique disparando: investida; Ctrl: esquiva com invencibilidade.
// Quando uma sequência para no meio, a animação de recuperação (se houver) devolve a arma à
// guarda; qualquer input a cancela.
//
// Os golpes são disparados por CrossFade a partir daqui (e não por triggers no Animator):
// o script é a única fonte da verdade sobre "em qual golpe estou", então lógica e animação
// nunca dessincronizam e não sobram triggers "presos" disparando golpes fora de hora.
//
// Espaçamento: os golpes dão passos longos. Com um alvo à frente, o passo é encurtado para o golpe
// terminar na distância ideal e, encostado, o resto do passo empurra o alvo; assim o personagem
// não fica "andando no lugar" com os pés deslizando. Com a mira travada (TargetLock) todo golpe
// mira o alvo travado.
//
// Arsenal: as armas pegas (F) ficam guardadas; 1 = punhos, 2..5 = armas, G larga a arma da mão.
[RequireComponent(typeof(ThirdPersonController))]
[DefaultExecutionOrder(-50)]
public class PlayerCombat : MonoBehaviour
{
    private enum ActionState { None, Attack, Recovery, Dodge, Stagger, Down }
    private enum Chain { Light, Heavy, Air, Special }
    private enum SlamPhase { None, Rising, Diving, Impact }

    [Header("Golpes")]
    [Tooltip("Golpes sem arma")]
    public WeaponMoveset unarmedMoveset;
    [Tooltip("Por quanto tempo um clique fica guardado esperando a hora de encadear o próximo golpe")]
    public float inputBufferTime = 0.35f;
    [Tooltip("Velocidade mínima (m/s) para o clique disparando virar investida")]
    public float dashAttackMinSpeed = 9f;

    [Header("Distância de ataque")]
    [Tooltip("Velocidade máxima do avanço automático até o alvo (m/s)")]
    public float lungeSpeed = 9f;
    [Tooltip("Só avança sozinho até alvos mais perto que isso")]
    public float lungeMaxDistance = 5f;
    [Tooltip("Menor fração do passo da animação mantida ao encurtar (menos que isso e os pés deslizam demais)")]
    [Range(0f, 1f)] public float minStepScale = 0.5f;
    [Tooltip("Mais perto que isso o corpo não avança: o que sobra do passo empurra o alvo (se ele puder ser empurrado) em vez de travar o personagem")]
    public float contactDistance = 0.95f;

    [Header("Esquiva")]
    public string dodgeStateUnarmed = "DodgeUnarmed";
    public string dodgeStateArmed   = "DodgeArmed";
    public AnimationClip dodgeClipUnarmed;
    public AnimationClip dodgeClipArmed;
    public float dodgeSpeed = 1.25f;
    public float dodgeRootMotionScale = 0.75f;
    [Tooltip("Janela de invencibilidade, em tempo normalizado")]
    public Vector2 dodgeInvulnerability = new Vector2(0.05f, 0.45f);
    [Range(0f, 1f)] public float dodgeCancelTime = 0.6f;
    [Range(0f, 1f)] public float dodgeEndTime = 0.8f;
    public float dodgeCooldown = 0.15f;

    [Header("Levar dano")]
    [Tooltip("Animação de dano (cambaleia para trás)")]
    public string hitReactionState = "HitReaction";
    public AnimationClip hitReactionClip;
    [Tooltip("A partir desta fração da animação de dano já dá para esquivar ou atacar")]
    [Range(0f, 1f)] public float hitReactionCancelTime = 0.45f;
    [Tooltip("Invencível por este tempo depois de levar um golpe (não apanha de novo na mesma hora)")]
    public float hurtInvulnerability = 0.5f;
    [Tooltip("Vida zerada: cai, espera e levanta com a vida cheia")]
    public string knockdownState = "PlayerKnockdown";
    public AnimationClip knockdownClip;
    public string getUpState = "PlayerGetUp";
    public AnimationClip getUpClip;
    public float getUpSpeed = 1.6f;
    public float downTime = 1.2f;

    [Header("Esquiva perfeita")]
    [Tooltip("Esquivar bem na hora do golpe inimigo deixa o tempo lento por este tempo (s, tempo real)")]
    public float perfectDodgeDuration = 0.6f;
    [Range(0.05f, 1f)] public float perfectDodgeTimeScale = 0.3f;

    [Header("Mira assistida")]
    public float targetSearchRadius = 6f;
    [Range(0f, 180f)] public float targetSearchAngle = 75f;
    [Tooltip("Velocidade de giro (graus/s) para alinhar com o alvo no começo do golpe")]
    public float attackTurnSpeed = 1080f;
    [Tooltip("Mira travada (opcional): todo golpe mira o alvo travado")]
    public TargetLock targetLock;

    [Header("Combate aéreo")]
    [Tooltip("Impulso para cima (m/s) no começo de cada golpe aéreo: o personagem 'quica' e fica no ar")]
    public float airPopVelocity = 2f;
    [Tooltip("Golpes aéreos por pulo (depois disso o personagem cai)")]
    public int maxAirAttacks = 7;
    [Tooltip("Depois do lançador: tempo com gravidade fraca para emendar o combo aéreo")]
    public float launcherHangTime = 0.5f;
    [Range(0f, 1f)] public float launcherHangGravity = 0.12f;
    [Tooltip("Velocidade (m/s) do mergulho do golpe de queda")]
    public float slamDiveSpeed = 30f;

    [Header("Acerto")]
    public LayerMask hitMask = ~0;
    [Tooltip("Raio da área de acerto dos socos")]
    public float unarmedHitRadius = 0.42f;
    [Range(0f, 180f)] public float hitArcAngle = 70f;
    [Tooltip("Vista de cima, a ponta da arma/punho precisa estar (ou ter acabado de passar) a menos deste ângulo da direção do alvo")]
    [Range(0f, 90f)] public float hitArcTipAngle = 40f;
    [Tooltip("Distância horizontal mínima da ponta até o corpo para valer (espada erguida acima da cabeça não conta)")]
    public float hitArcMinTipDistance = 0.4f;
    [Tooltip("A área generosa só pega alvos até esta diferença de altura (alvos lá no alto, lançados, ficam de fora)")]
    public float hitArcMaxHeight = 2.2f;

    [Header("Feedback")]
    public float hitStopDuration = 0.055f;
    public float heavyHitStopDuration = 0.11f;
    public float cameraShake = 0.15f;
    public float heavyCameraShake = 0.35f;
    [Tooltip("Velocidade da troca de pose ao equipar/largar a arma")]
    public float weaponBlendSpeed = 4f;

    public event Action<AttackData, int> AttackStarted;
    public event Action SwingStarted;
    public event Action<DamageInfo, Health> HitLanded;
    public event Action DodgeStarted;
    public event Action<MeleeWeapon> WeaponChanged;
    public event Action ArsenalChanged;
    public event Action<Vector3> SlamLanded;
    public event Action<DamageInfo> Hurt;
    public event Action PerfectDodged;

    private static readonly int LocomotionHash  = Animator.StringToHash("Locomotion");
    private static readonly int JumpFallHash    = Animator.StringToHash("JumpFall");
    private static readonly int ActionSpeedHash = Animator.StringToHash("ActionSpeed");
    private static readonly int WeaponBlendHash = Animator.StringToHash("WeaponBlend");

    private ThirdPersonController controller;
    private PlayerInputReader input;
    private Animator animator;
    private Health health;
    private MeleeWeapon weapon;
    private readonly List<MeleeWeapon> arsenal = new List<MeleeWeapon>();
    private readonly List<WeaponPickup> nearbyPickups = new List<WeaponPickup>();

    private ActionState state;
    private Chain chain;
    private AttackData currentAttack;
    private int comboIndex = -1;
    private float actionTime;
    private float actionDuration = 1f;
    private float playbackRate = 1f;
    private float lastNormalizedTime;
    private float attackBufferedUntil   = -1f;
    private float heavyBufferedUntil    = -1f;
    private float launcherBufferedUntil = -1f;
    private float dodgeBufferedUntil    = -1f;
    private float dodgeReadyTime;
    private Vector3 steerDirection;
    private Health attackTarget;
    private IPushable attackPushable;
    private float stepScale = 1f;
    private Vector3 carryMomentum;
    private float weaponBlend;
    private float poseBlend;
    private int airAttacksUsed;
    private SlamPhase slamPhase;
    private float hurtInvulnerableUntil;
    private Vector3 hurtKnockVelocity;
    private bool gettingUp;
    private float perfectDodgeReadyTime;

    // Quão rápido (m/s por segundo) o embalo herdado de um ataque em corrida se dissipa
    private const float momentumDecay = 35f;

    // Detecção de acerto: até duas lâminas (adagas duplas), cada uma varre o espaço entre frames
    private struct Blade
    {
        public bool valid;
        public Vector3 a, b;
        public float radius;
        public bool hasPrev;
        public Vector3 prevA, prevB;
        public bool hasPrevTip;
        public Vector3 prevTip;
    }

    private readonly Blade[] blades = new Blade[2];
    private int swingWindow = -1;
    private readonly HashSet<Health> hitThisWindow = new HashSet<Health>();
    private readonly Collider[] overlapBuffer = new Collider[32];

    public PlayerInputReader Input => input;
    public MeleeWeapon Weapon => weapon;
    public IReadOnlyList<MeleeWeapon> Arsenal => arsenal;
    public bool IsArmed => weapon != null;
    public bool IsBusy => state != ActionState.None && state != ActionState.Recovery;
    public bool IsAttacking => state == ActionState.Attack;
    public bool IsDodging => state == ActionState.Dodge;
    public bool IsStaggered => state == ActionState.Stagger || state == ActionState.Down;
    public bool IsAirAttacking => state == ActionState.Attack && (chain == Chain.Air || slamPhase != SlamPhase.None);
    public int ComboIndex => comboIndex;
    public AttackData CurrentAttack => state == ActionState.Attack ? currentAttack : null;
    public float NormalizedTime => actionDuration > 0f ? actionTime / actionDuration : 1f;
    // 0 = mão aberta, 1 = mão fechada na arma (transição suave ao pegar/largar)
    public float WeaponBlend => weaponBlend;
    public WeaponMoveset CurrentMoveset => weapon != null && weapon.moveset != null ? weapon.moveset : unarmedMoveset;
    // Postura da locomoção (Motion Matching): 0 desarmado, 1 espada de duas mãos, 2 arma de uma mão
    public int LocomotionStance => CurrentMoveset != null ? CurrentMoveset.locomotionStance : 0;
    // Alvo que o próximo golpe vai mirar (o HUD mostra um marcador nele)
    public Health PreviewTarget { get; private set; }

    private bool AttackBuffered   => Time.time <= attackBufferedUntil;
    private bool HeavyBuffered    => Time.time <= heavyBufferedUntil;
    private bool LauncherBuffered => Time.time <= launcherBufferedUntil;
    private bool DodgeBuffered    => Time.time <= dodgeBufferedUntil;
    private bool AnyAttackBuffered => AttackBuffered || HeavyBuffered || LauncherBuffered;

    private void Awake()
    {
        controller = GetComponent<ThirdPersonController>();
        input      = GetComponent<PlayerInputReader>();
        animator   = GetComponent<Animator>();
        health     = GetComponent<Health>();
        if (targetLock == null)
            targetLock = GetComponent<TargetLock>();
    }

    private void OnEnable()
    {
        controller.ActionMotionFilter = FilterActionMotion;
        if (health != null)
            health.Damaged += OnDamaged;
    }

    private void OnDisable()
    {
        if (health != null)
            health.Damaged -= OnDamaged;
        if (state != ActionState.None)
            EndAction(0.1f);
        if (controller.ActionMotionFilter == (Func<Vector3, Vector3>)FilterActionMotion)
            controller.ActionMotionFilter = null;
        CombatHUD.SetPrompt(this, null);
    }

    // ---- Pedidos (input do jogador ou código/testes) ----

    public void RequestAttack()   { attackBufferedUntil   = Time.time + inputBufferTime; }
    public void RequestHeavy()    { heavyBufferedUntil    = Time.time + inputBufferTime; }
    public void RequestLauncher() { launcherBufferedUntil = Time.time + inputBufferTime; }
    public void RequestDodge()    { dodgeBufferedUntil    = Time.time + inputBufferTime * 0.6f; }

    private void ClearAttackBuffers()
    {
        attackBufferedUntil   = -1f;
        heavyBufferedUntil    = -1f;
        launcherBufferedUntil = -1f;
    }

    // ---- Arsenal ----

    public void RegisterPickup(WeaponPickup pickup)
    {
        if (pickup != null && !nearbyPickups.Contains(pickup))
            nearbyPickups.Add(pickup);
    }

    public void UnregisterPickup(WeaponPickup pickup)
    {
        nearbyPickups.Remove(pickup);
    }

    // Chamado pelo WeaponPickup depois de prender a arma na mão. Uma arma no mesmo atalho é largada.
    public void AddWeapon(MeleeWeapon newWeapon)
    {
        if (newWeapon == null || arsenal.Contains(newWeapon))
            return;
        MeleeWeapon sameSlot = arsenal.Find(w => w != null && w.slot == newWeapon.slot);
        if (sameSlot != null)
            DropWeapon(sameSlot);
        arsenal.Add(newWeapon);
        arsenal.Sort((a, b) => a.slot.CompareTo(b.slot));
        SwitchWeapon(newWeapon);
        ArsenalChanged?.Invoke();
    }

    public void SwitchWeapon(MeleeWeapon next)
    {
        if (next != null && !arsenal.Contains(next))
            return;
        if (state != ActionState.None)
            EndAction(0.15f);
        if (weapon != null)
            weapon.SetTrail(false);
        weapon = next;
        foreach (MeleeWeapon w in arsenal)
            if (w != null && w.TryGetComponent(out WeaponPickup pickup))
                pickup.SetHolstered(w != weapon);
        WeaponChanged?.Invoke(weapon);
    }

    // Tecla numérica: 1 = punhos, as outras são o atalho de cada arma (MeleeWeapon.slot)
    public void SwitchToSlot(int slot)
    {
        if (slot == 1)
        {
            if (weapon != null)
                SwitchWeapon(null);
            return;
        }
        MeleeWeapon target = arsenal.Find(w => w != null && w.slot == slot);
        if (target != null && target != weapon)
            SwitchWeapon(target);
    }

    public void DropWeapon(MeleeWeapon dropped)
    {
        if (dropped == null || !arsenal.Contains(dropped))
            return;
        if (dropped == weapon)
            SwitchWeapon(null);
        arsenal.Remove(dropped);
        if (dropped.TryGetComponent(out WeaponPickup pickup))
            pickup.Release(this);
        ArsenalChanged?.Invoke();
    }

    private void UpdateArsenalInput()
    {
        bool free = state == ActionState.None || state == ActionState.Recovery;
        WeaponPickup nearest = null;
        float best = float.MaxValue;
        for (int i = nearbyPickups.Count - 1; i >= 0; i--)
        {
            WeaponPickup p = nearbyPickups[i];
            if (p == null || !p.isActiveAndEnabled || p.IsEquipped)
            {
                nearbyPickups.RemoveAt(i);
                continue;
            }
            float d = (p.transform.position - transform.position).sqrMagnitude;
            if (d < best)
            {
                best    = d;
                nearest = p;
            }
        }

        bool canPick = nearest != null && free && controller.IsGrounded;
        CombatHUD.SetPrompt(this, canPick ? $"[F] Pegar {nearest.Weapon.DisplayName}" : null);
        if (input == null)
            return;
        if (canPick && input.InteractPressed)
        {
            nearest.Equip(this);
            return;
        }
        if (input.DropPressed && weapon != null && free)
            DropWeapon(weapon);
        else if (input.WeaponSlotPressed > 0 && (free || (state == ActionState.Attack && NormalizedTime >= currentAttack.recoveryTime)))
            SwitchToSlot(input.WeaponSlotPressed);
    }

    // ---- Loop ----

    private void Update()
    {
        if (input != null)
        {
            if (input.AttackPressed)   RequestAttack();
            if (input.HeavyPressed)    RequestHeavy();
            if (input.LauncherPressed) RequestLauncher();
            if (input.DodgePressed)    RequestDodge();
        }

        UpdateArsenalInput();
        UpdateWeaponBlend(Time.deltaTime);
        if (controller.IsGrounded && state != ActionState.Attack)
            airAttacksUsed = 0;
        PreviewTarget = state == ActionState.None ? FindTarget(DesiredDirection()) : attackTarget;

        switch (state)
        {
            case ActionState.None:
                TryStartFromNeutral();
                break;
            case ActionState.Attack:
                UpdateAttack(Time.deltaTime);
                break;
            case ActionState.Recovery:
                UpdateRecovery(Time.deltaTime);
                break;
            case ActionState.Dodge:
                UpdateDodge(Time.deltaTime);
                break;
            case ActionState.Stagger:
                UpdateStagger(Time.deltaTime);
                break;
            case ActionState.Down:
                UpdateDown(Time.deltaTime);
                break;
        }

        // Logo depois de levar um golpe (ou caído) não apanha de novo
        if (health != null && (Time.time < hurtInvulnerableUntil || state == ActionState.Down))
            health.Invulnerable = true;
    }

    private void LateUpdate()
    {
        // Roda depois da animação deste frame: os ossos/arma já estão na pose atual.
        if (state != ActionState.Attack || currentAttack == null)
        {
            if (swingWindow >= 0) ResetSwing();
            return;
        }

        float n    = NormalizedTime;
        int window = FindActiveWindow(currentAttack, lastNormalizedTime, n);
        lastNormalizedTime = n;

        if (weapon != null)
            weapon.SetTrail(IsNearAnyWindow(currentAttack, n) || slamPhase == SlamPhase.Diving);

        if (window != swingWindow)
        {
            swingWindow = window;
            for (int i = 0; i < blades.Length; i++)
            {
                blades[i].hasPrev    = false;
                blades[i].hasPrevTip = false;
            }
            hitThisWindow.Clear();
            if (window >= 0)
                SwingStarted?.Invoke();
        }
        if (window < 0)
            return;

        UpdateBlades();
        for (int i = 0; i < blades.Length && state == ActionState.Attack; i++)
        {
            ref Blade blade = ref blades[i];
            if (!blade.valid)
                continue;
            if (!blade.hasPrev)
            {
                blade.prevA   = blade.a;
                blade.prevB   = blade.b;
                blade.hasPrev = true;
            }
            Sweep(blade.prevA, blade.prevB, blade.a, blade.b, blade.radius);
            blade.prevA = blade.a;
            blade.prevB = blade.b;
            if (state == ActionState.Attack)
                CheckHitArc(ref blade);
        }
    }

    // ---- Começo dos golpes ----

    private void TryStartFromNeutral()
    {
        WeaponMoveset set = CurrentMoveset;
        if (DodgeBuffered && CanStartDodge())
        {
            StartDodge();
            return;
        }
        if (set == null || !AnyAttackBuffered)
            return;

        if (controller.IsGrounded)
        {
            if (LauncherBuffered && IsValid(set.launcher))
                StartAttack(set.launcher, Chain.Special, 0);
            else if (AttackBuffered)
            {
                AttackData dash = DashAttackAvailable(set);
                if (dash != null)
                    StartAttack(dash, Chain.Special, 0);
                else
                    StartChain(Chain.Light, 0);
            }
            else if (HeavyBuffered)
                StartChain(Chain.Heavy, 0);
        }
        else
        {
            if (HeavyBuffered && IsValid(set.slam))
                StartAttack(set.slam, Chain.Special, 0);
            else if (AttackBuffered && airAttacksUsed < maxAirAttacks)
                StartChain(Chain.Air, 0);
        }
    }

    private static bool IsValid(AttackData attack)
    {
        return attack != null && !string.IsNullOrEmpty(attack.stateName);
    }

    private AttackData[] ChainAttacks(Chain which)
    {
        WeaponMoveset set = CurrentMoveset;
        if (set == null)
            return null;
        switch (which)
        {
            case Chain.Light: return set.lightCombo;
            case Chain.Heavy: return set.heavyCombo;
            case Chain.Air:   return set.airCombo;
            default:          return null;
        }
    }

    private bool StartChain(Chain which, int index)
    {
        AttackData[] attacks = ChainAttacks(which);
        if (attacks == null || attacks.Length == 0)
            return false;
        index = Mathf.Clamp(index, 0, attacks.Length - 1);
        if (!IsValid(attacks[index]))
            return false;
        StartAttack(attacks[index], which, index);
        return true;
    }

    // Investida: clique disparando, em velocidade alta.
    private AttackData DashAttackAvailable(WeaponMoveset set)
    {
        if (!IsValid(set.dashAttack) || !controller.IsSprinting)
            return null;
        Vector3 velocity = controller.Controller.velocity;
        velocity.y = 0f;
        return velocity.magnitude >= dashAttackMinSpeed ? set.dashAttack : null;
    }

    private void StartAttack(AttackData attack, Chain which, int index)
    {
        if (!IsValid(attack))
            return;

        // O embalo de um ataque em corrida não some de uma vez ao encadear o próximo golpe.
        carryMomentum = state == ActionState.Attack ? transform.forward * CarrySpeed() : Vector3.zero;
        bool grounded = controller.IsGrounded;

        ResetSwing();
        state               = ActionState.Attack;
        chain               = which;
        comboIndex          = index;
        currentAttack       = attack;
        actionTime          = 0f;
        playbackRate        = 1f;
        lastNormalizedTime  = 0f;
        actionDuration      = attack.Duration;
        stepScale           = 1f;
        slamPhase           = attack.IsSlam ? SlamPhase.Rising : SlamPhase.None;
        ClearAttackBuffers();
        if (health != null)
            health.Invulnerable = false;

        // Mira: o alvo travado, ou o mais próximo na direção do input (ou da frente do personagem).
        steerDirection = DesiredDirection();
        attackTarget   = FindTarget(steerDirection);
        attackPushable = attackTarget != null ? attackTarget.GetComponent<IPushable>() : null;
        UpdateSteerDirection();

        controller.SetActionLock(true, attack.rootMotionScale, attack.verticalRootMotion);
        if (!grounded || attack.IsSlam)
        {
            // No ar: cada golpe dá um pequeno impulso para cima e a gravidade fica fraca enquanto ele dura;
            // golpes seguidos sustentam cada vez menos, até o personagem cair.
            controller.GravityScale = attack.airGravityScale;
            float fade = 1f - airAttacksUsed / (float)(maxAirAttacks + 1);
            float pop  = attack.IsSlam ? airPopVelocity * 2f : airPopVelocity * fade;
            if (controller.VerticalVelocity < pop)
                controller.SetVerticalVelocity(pop);
            airAttacksUsed++;
        }

        if (animator != null)
        {
            animator.SetFloat(ActionSpeedHash, attack.speed);
            animator.CrossFadeInFixedTime(attack.stateName, attack.transitionDuration, 0, 0f);
        }
        AttackStarted?.Invoke(attack, index);
    }

    // ---- Durante o golpe ----

    private void UpdateAttack(float dt)
    {
        actionTime += dt * playbackRate;
        float n = NormalizedTime;
        AttackData attack = currentAttack;
        carryMomentum = Vector3.MoveTowards(carryMomentum, Vector3.zero, momentumDecay * dt);

        // Alinha com o alvo só antes do golpe sair; depois a direção fica travada.
        float steerEnd = attack.FirstHitWindowStart;
        if (n < steerEnd)
        {
            UpdateSteerDirection();
            controller.SetFacing(steerDirection, attackTurnSpeed);
        }
        else
            controller.ClearFacing();

        // Perto do alvo: o passo da animação é encurtado (FilterActionMotion) para terminar na distância ideal.
        // Longe (e antes do golpe sair): avança até a distância de ataque, para o golpe conectar.
        // O ataque em corrida já chega no embalo, então não avança sozinho.
        WeaponMoveset set = CurrentMoveset;
        float range    = set != null ? set.attackRange : 2f;
        float distance = DistanceToTarget(out Vector3 toTarget);
        stepScale = ComputeStepScale(attack, n, distance);
        if (attack.carrySpeed <= 0f && slamPhase == SlamPhase.None && n < steerEnd && distance > range && distance < lungeMaxDistance)
            controller.Nudge(toTarget * Mathf.Min(lungeSpeed * dt, distance - range));

        if (slamPhase == SlamPhase.Rising || slamPhase == SlamPhase.Diving)
        {
            UpdateSlam();
            return;
        }
        else if (chain == Chain.Air && controller.IsGrounded && actionTime > 0.1f)
        {
            // Golpe aéreo que tocou o chão: pousa
            EndAction(0.15f);
            return;
        }

        if (TryContinueCombo(attack, n))
            return;

        if (DodgeBuffered && n >= attack.LastHitWindowEnd && CanStartDodge())
        {
            StartDodge();
            return;
        }

        bool wantsToMove = controller.HasMoveInput || (input != null && input.JumpPressed);
        if (n >= attack.recoveryTime && wantsToMove)
        {
            EndAction(0.2f);
            return;
        }

        if (n >= attack.endTime)
        {
            if (controller.IsGrounded && !string.IsNullOrEmpty(attack.recoveryState) && attack.recoveryClip != null)
                StartRecovery(attack);
            else
                EndAction(0.25f);
        }
    }

    // Encadeia o próximo golpe se houver clique guardado e já for hora.
    private bool TryContinueCombo(AttackData attack, float n)
    {
        if (!AnyAttackBuffered || n < attack.comboTime)
            return false;
        WeaponMoveset set = CurrentMoveset;
        if (set == null)
            return false;
        bool grounded = controller.IsGrounded;

        if (!grounded)
        {
            if (HeavyBuffered && IsValid(set.slam))
            {
                StartAttack(set.slam, Chain.Special, 0);
                return true;
            }
            if (AttackBuffered && airAttacksUsed < maxAirAttacks)
            {
                int next = chain == Chain.Air ? comboIndex + 1 : 0;
                AttackData[] air = set.airCombo;
                if (air == null || air.Length == 0)
                    return false;
                if (next >= air.Length)
                {
                    if (n < attack.recoveryTime)
                        return false;
                    next = 0;
                }
                return StartChain(Chain.Air, next);
            }
            return false;
        }

        // Cancelar no lançador no meio de uma sequência (estilo DMC)
        if (LauncherBuffered && chain != Chain.Special && IsValid(set.launcher))
        {
            StartAttack(set.launcher, Chain.Special, 0);
            return true;
        }
        Chain wanted = AttackBuffered ? Chain.Light : (HeavyBuffered ? Chain.Heavy : chain);
        if (wanted != Chain.Light && wanted != Chain.Heavy)
            return false;
        AttackData[] attacks = ChainAttacks(wanted);
        if (attacks == null || attacks.Length == 0)
            return false;

        int index;
        if (chain == wanted)
        {
            index = comboIndex + 1;
            if (index >= attacks.Length)
            {
                // Depois do finalizador, a sequência recomeça só quando a recuperação acaba
                if (n < attack.recoveryTime)
                    return false;
                index = 0;
            }
        }
        else if (chain == Chain.Special || chain == Chain.Air)
            index = 0;
        else
            index = Mathf.Min(comboIndex + 1, attacks.Length - 1); // troca de sequência: continua do mesmo ponto
        return StartChain(wanted, index);
    }

    // ---- Golpe de queda ----

    private void UpdateSlam()
    {
        AttackData attack = currentAttack;
        switch (slamPhase)
        {
            case SlamPhase.Rising:
                if (controller.IsGrounded && actionTime > 0.12f)
                    SlamImpact();
                else if (NormalizedTime >= attack.slamHoldTime)
                {
                    // Congela a pose de ataque e despenca
                    slamPhase    = SlamPhase.Diving;
                    playbackRate = 0f;
                    actionTime   = attack.slamHoldTime * actionDuration;
                    if (animator != null)
                        animator.SetFloat(ActionSpeedHash, 0f);
                    controller.GravityScale = 1f;
                    controller.SetVerticalVelocity(-slamDiveSpeed);
                }
                break;
            case SlamPhase.Diving:
                if (controller.IsGrounded)
                    SlamImpact();
                break;
        }
    }

    private void SlamImpact()
    {
        AttackData attack = currentAttack;
        slamPhase    = SlamPhase.Impact;
        playbackRate = 1f;
        actionTime   = Mathf.Max(actionTime, attack.slamImpactTime * actionDuration);
        if (animator != null)
        {
            animator.SetFloat(ActionSpeedHash, attack.speed);
            animator.Play(attack.stateName, 0, attack.slamImpactTime);
        }

        // Onda de choque: acerta todo mundo em volta (quem está no ar é jogado no chão)
        Vector3 center = transform.position;
        for (int i = Health.Active.Count - 1; i >= 0; i--)
        {
            Health target = Health.Active[i];
            if (target == null || target == health || !target.CanBeHit)
                continue;
            Vector3 offset = target.transform.position - center;
            float height = offset.y;
            offset.y = 0f;
            if (offset.magnitude > attack.slamRadius || height < -1f || height > 3f)
                continue;
            ApplyHit(target, target.transform.position + Vector3.up * 0.6f, HitReaction.Slam, attack.knockback);
            if (state != ActionState.Attack)
                break;
        }
        ThirdPersonCamera.Shake(heavyCameraShake);
        HitEffects.Spawn(center + transform.forward * 0.8f + Vector3.up * 0.1f, Vector3.up, true);
        SlamLanded?.Invoke(center);
    }

    // ---- Recuperação ----

    private void StartRecovery(AttackData from)
    {
        ResetSwing();
        state          = ActionState.Recovery;
        chain          = Chain.Light;
        currentAttack  = null;
        attackTarget   = null;
        attackPushable = null;
        comboIndex     = -1;
        actionTime     = 0f;
        playbackRate   = 1f;
        actionDuration = from.recoveryClip.length;
        controller.SetActionLock(true, from.rootMotionScale);
        controller.ClearFacing();
        if (animator != null)
        {
            animator.SetFloat(ActionSpeedHash, 1f);
            animator.CrossFadeInFixedTime(from.recoveryState, 0.08f, 0, 0f);
        }
    }

    private void UpdateRecovery(float dt)
    {
        actionTime += dt;
        if (DodgeBuffered && CanStartDodge())
        {
            StartDodge();
            return;
        }
        if (AnyAttackBuffered)
        {
            state = ActionState.None;
            TryStartFromNeutral();
            if (state == ActionState.Attack)
                return;
            state = ActionState.Recovery;
        }
        bool wantsToMove = controller.HasMoveInput || (input != null && input.JumpPressed);
        if (wantsToMove || !controller.IsGrounded)
        {
            EndAction(0.2f);
            return;
        }
        if (NormalizedTime >= 0.95f)
            EndAction(0.2f);
    }

    // ---- Alvo ----

    // Distância horizontal até o alvo do golpe atual (infinito se não houver alvo).
    private float DistanceToTarget(out Vector3 direction)
    {
        direction = transform.forward;
        if (attackTarget == null || attackTarget.IsDead)
            return float.PositiveInfinity;
        Vector3 toTarget = attackTarget.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance > 0.001f)
            direction = toTarget / distance;
        return distance;
    }

    // O alvo pode ter sido empurrado pelo golpe anterior: reaponta enquanto ainda dá para girar.
    private void UpdateSteerDirection()
    {
        if (DistanceToTarget(out Vector3 toTarget) < float.PositiveInfinity)
            steerDirection = toTarget;
    }

    private Vector3 DesiredDirection()
    {
        Health locked = LockedTarget();
        if (locked != null)
        {
            Vector3 toTarget = locked.transform.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.01f)
                return toTarget.normalized;
        }
        return controller.HasMoveInput ? controller.MoveDirection.normalized : transform.forward;
    }

    private Health LockedTarget()
    {
        if (targetLock == null || targetLock.Target == null || targetLock.Target.IsDead)
            return null;
        return targetLock.Target;
    }

    private Health FindTarget(Vector3 direction)
    {
        Health locked = LockedTarget();
        if (locked != null)
        {
            Vector3 toLocked = locked.transform.position - transform.position;
            toLocked.y = 0f;
            if (toLocked.magnitude <= targetSearchRadius * 1.5f)
                return locked;
        }

        Health best     = null;
        float bestScore = float.MaxValue;
        foreach (Health candidate in Health.Active)
        {
            if (candidate == null || candidate == health || candidate.IsDead)
                continue;

            Vector3 toTarget = candidate.transform.position - transform.position;
            float height = toTarget.y;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance > targetSearchRadius || distance < 0.01f || height > 4f)
                continue;

            float angle = Vector3.Angle(direction, toTarget);
            if (angle > targetSearchAngle)
                continue;

            // Distância pesa mais que ângulo (20 graus ~ 1 metro).
            float score = distance + angle * 0.05f;
            if (score < bestScore)
            {
                bestScore = score;
                best      = candidate;
            }
        }
        return best;
    }

    // ---- Espaçamento até o alvo ----

    // Fração do passo da animação que vale agora, para o golpe terminar perto da distância ideal do alvo.
    private float ComputeStepScale(AttackData attack, float n, float distance)
    {
        if (attack.rootTravel <= 0f || float.IsInfinity(distance) || animator == null)
            return 1f;
        // Quanto a animação ainda vai andar (aproximação linear no tempo) e quanto espaço sobra até a distância ideal.
        // Recalculado todo frame: se o alvo for empurrado pelo golpe, o passo volta a crescer.
        float remaining = attack.rootTravel * animator.humanScale * attack.rootMotionScale * Mathf.Max(0.1f, 1f - n);
        float ideal     = CurrentMoveset != null ? CurrentMoveset.idealDistance : 1.2f;
        return Mathf.Clamp((distance - ideal) / Mathf.Max(0.01f, remaining), minStepScale, 1f);
    }

    // Embalo do golpe atual (m/s): ataques em corrida seguem avançando na velocidade da passada.
    private float CarrySpeed()
    {
        return state == ActionState.Attack && currentAttack != null ? Mathf.Max(0f, currentAttack.carrySpeed) : 0f;
    }

    // Chamado pelo ThirdPersonController (OnAnimatorMove) com o deslocamento da ação neste frame.
    private Vector3 FilterActionMotion(Vector3 delta)
    {
        if (state != ActionState.Attack || currentAttack == null)
            return delta;

        delta += (transform.forward * CarrySpeed() + carryMomentum) * Time.deltaTime;

        float distance = DistanceToTarget(out Vector3 toTarget);
        if (float.IsInfinity(distance))
            return delta;

        // Só o que aproxima do alvo é ajustado; recuos e passos de lado ficam como na animação.
        float forward = Vector3.Dot(delta, toTarget);
        if (forward <= 0f)
            return delta;
        Vector3 side = delta - toTarget * forward;
        forward *= stepScale;

        // Encostado: o corpo para e o que sobra do passo empurra o alvo (se ele puder ser empurrado).
        float free = Mathf.Max(0f, distance - contactDistance);
        if (forward > free)
        {
            float pushed = 0f;
            if (attackPushable != null && !attackTarget.IsDead)
                pushed = Mathf.Max(0f, Vector3.Dot(attackPushable.Push(toTarget * (forward - free)), toTarget));
            forward = Mathf.Min(forward, free + pushed);
        }
        return side + toTarget * forward;
    }

    // ---- Esquiva ----

    private void GetDodge(out string stateName, out AnimationClip clip)
    {
        WeaponMoveset set = CurrentMoveset;
        if (set != null && !string.IsNullOrEmpty(set.dodgeState) && set.dodgeClip != null)
        {
            stateName = set.dodgeState;
            clip      = set.dodgeClip;
            return;
        }
        bool armedPose = set != null && set.animatorPose > 0.5f;
        stateName = armedPose ? dodgeStateArmed : dodgeStateUnarmed;
        clip      = armedPose ? dodgeClipArmed : dodgeClipUnarmed;
    }

    private bool CanStartDodge()
    {
        GetDodge(out _, out AnimationClip clip);
        return clip != null && controller.IsGrounded && Time.time >= dodgeReadyTime;
    }

    private void StartDodge()
    {
        GetDodge(out string stateName, out AnimationClip clip);

        ResetSwing();
        // A esquiva vai para onde o input aponta (mesmo com a mira travada)
        controller.SnapFacing(controller.HasMoveInput ? controller.MoveDirection.normalized : transform.forward);
        controller.ClearFacing();

        state               = ActionState.Dodge;
        currentAttack       = null;
        attackTarget        = null;
        attackPushable      = null;
        carryMomentum       = Vector3.zero;
        comboIndex          = -1;
        actionTime          = 0f;
        playbackRate        = 1f;
        slamPhase           = SlamPhase.None;
        actionDuration      = clip.length / Mathf.Max(0.05f, dodgeSpeed);
        dodgeBufferedUntil  = -1f;
        ClearAttackBuffers();
        dodgeReadyTime      = Time.time + actionDuration * dodgeCancelTime + dodgeCooldown;

        controller.SetActionLock(true, dodgeRootMotionScale);
        if (animator != null)
        {
            animator.SetFloat(ActionSpeedHash, dodgeSpeed);
            animator.CrossFadeInFixedTime(stateName, 0.05f, 0, 0f);
        }
        DodgeStarted?.Invoke();
    }

    private void UpdateDodge(float dt)
    {
        actionTime += dt;
        float n = NormalizedTime;

        if (health != null)
            health.Invulnerable = n >= dodgeInvulnerability.x && n <= dodgeInvulnerability.y;

        if (n >= dodgeCancelTime)
        {
            // Esquiva -> ataque é um cancelamento clássico
            if (AnyAttackBuffered)
            {
                state = ActionState.None;
                if (health != null)
                    health.Invulnerable = false;
                TryStartFromNeutral();
                if (state != ActionState.None)
                    return;
                state = ActionState.Dodge;
            }
            if (DodgeBuffered && CanStartDodge())
            {
                StartDodge();
                return;
            }
            if (controller.HasMoveInput || (input != null && input.JumpPressed))
            {
                EndAction(0.2f);
                return;
            }
        }

        if (n >= dodgeEndTime)
            EndAction(0.25f);
    }

    // ---- Levar dano ----

    private void OnDamaged(DamageInfo info)
    {
        Hurt?.Invoke(info);
        ThirdPersonCamera.Shake(info.heavy ? 0.35f : 0.2f);
        if (health.IsDead)
            StartDown(info);
        else
            StartStagger(info);
    }

    // Limpa o golpe/esquiva em andamento antes de uma reação de dano
    private void InterruptAction()
    {
        ResetSwing();
        controller.ClearFacing();
        currentAttack  = null;
        attackTarget   = null;
        attackPushable = null;
        carryMomentum  = Vector3.zero;
        comboIndex     = -1;
        playbackRate   = 1f;
        slamPhase      = SlamPhase.None;
        actionTime     = 0f;
        ClearAttackBuffers();
        dodgeBufferedUntil = -1f;
    }

    private void StartStagger(DamageInfo info)
    {
        InterruptAction();
        state = ActionState.Stagger;
        // Vira para quem bateu e é empurrado para trás
        if (info.direction.sqrMagnitude > 1e-4f)
            controller.SnapFacing(-info.direction);
        hurtKnockVelocity     = info.direction * (2f * Mathf.Max(info.knockback, 0.3f) / 0.25f);
        hurtInvulnerableUntil = Time.time + hurtInvulnerability;
        actionDuration        = hitReactionClip != null ? hitReactionClip.length : 0.8f;
        controller.SetActionLock(true, 0f);
        if (animator != null && !string.IsNullOrEmpty(hitReactionState))
        {
            animator.SetFloat(ActionSpeedHash, 1f);
            animator.CrossFadeInFixedTime(hitReactionState, 0.05f, 0, 0f);
        }
    }

    private void UpdateStagger(float dt)
    {
        actionTime += dt;
        float n = NormalizedTime;
        PushBack(dt);

        if (n >= hitReactionCancelTime)
        {
            if (DodgeBuffered && CanStartDodge())
            {
                StartDodge();
                return;
            }
            if (AnyAttackBuffered)
            {
                state = ActionState.None;
                TryStartFromNeutral();
                if (state != ActionState.None)
                    return;
                state = ActionState.Stagger;
            }
            if (n >= 0.7f && controller.HasMoveInput)
            {
                EndAction(0.25f);
                return;
            }
        }
        if (n >= 0.92f)
            EndAction(0.25f);
    }

    // Empurrão que cai linearmente até zero em 0,25 s
    private void PushBack(float dt)
    {
        if (hurtKnockVelocity.sqrMagnitude < 1e-4f)
            return;
        controller.Nudge(hurtKnockVelocity * dt);
        hurtKnockVelocity = Vector3.MoveTowards(hurtKnockVelocity, Vector3.zero, hurtKnockVelocity.magnitude / 0.25f * dt + 1e-3f);
    }

    private void StartDown(DamageInfo info)
    {
        InterruptAction();
        state     = ActionState.Down;
        gettingUp = false;
        if (info.direction.sqrMagnitude > 1e-4f)
            controller.SnapFacing(-info.direction);
        hurtKnockVelocity = info.direction * (2f * Mathf.Max(info.knockback, 1f) / 0.25f);
        actionDuration    = (knockdownClip != null ? knockdownClip.length : 1.5f) + downTime;
        controller.SetActionLock(true, 0f);
        if (animator != null && !string.IsNullOrEmpty(knockdownState))
        {
            animator.SetFloat(ActionSpeedHash, 1f);
            animator.CrossFadeInFixedTime(knockdownState, 0.06f, 0, 0f);
        }
    }

    private void UpdateDown(float dt)
    {
        actionTime += dt;
        PushBack(dt);
        if (!gettingUp && actionTime >= actionDuration)
        {
            // Levanta com a vida cheia (invencível até terminar)
            gettingUp      = true;
            actionTime     = 0f;
            actionDuration = (getUpClip != null ? getUpClip.length : 2f) / Mathf.Max(0.05f, getUpSpeed);
            health.ResetHealth();
            if (animator != null && !string.IsNullOrEmpty(getUpState))
            {
                animator.SetFloat(ActionSpeedHash, getUpSpeed);
                animator.CrossFadeInFixedTime(getUpState, 0.15f, 0, 0f);
            }
            return;
        }
        if (gettingUp && actionTime >= actionDuration * 0.92f)
        {
            hurtInvulnerableUntil = Time.time + hurtInvulnerability;
            EndAction(0.3f);
        }
    }

    // Chamado por um inimigo quando o golpe dele pegaria o player bem no meio da esquiva.
    public void NotifyPerfectDodge()
    {
        if (Time.unscaledTime < perfectDodgeReadyTime)
            return;
        perfectDodgeReadyTime = Time.unscaledTime + perfectDodgeDuration + 0.5f;
        HitStop.RequestSlowMotion(perfectDodgeDuration, perfectDodgeTimeScale, 0.35f);
        PerfectDodged?.Invoke();
    }

    // ---- Comum ----

    private void EndAction(float transition)
    {
        // Saindo de um golpe com embalo (ataque em corrida): continua na mesma velocidade em vez de parar seco.
        Vector3 momentum = transform.forward * CarrySpeed() + carryMomentum;
        bool fromLauncher = state == ActionState.Attack && currentAttack != null && currentAttack.verticalRootMotion;

        ResetSwing();
        state          = ActionState.None;
        currentAttack  = null;
        attackTarget   = null;
        attackPushable = null;
        carryMomentum  = Vector3.zero;
        comboIndex     = -1;
        playbackRate   = 1f;
        slamPhase      = SlamPhase.None;
        if (health != null)
            health.Invulnerable = false;

        controller.SetActionLock(false);
        if (momentum.sqrMagnitude > 0.01f)
            controller.SetMomentum(momentum);
        bool airborne = !controller.IsGrounded;
        if (airborne && fromLauncher)
            controller.Hang(launcherHangTime, launcherHangGravity);
        if (animator != null && isActiveAndEnabled)
        {
            animator.SetFloat(ActionSpeedHash, 1f);
            animator.CrossFadeInFixedTime(airborne ? JumpFallHash : LocomotionHash, transition, 0);
        }
    }

    private void UpdateWeaponBlend(float dt)
    {
        weaponBlend = Mathf.MoveTowards(weaponBlend, IsArmed ? 1f : 0f, weaponBlendSpeed * dt);
        float pose  = CurrentMoveset != null ? CurrentMoveset.animatorPose : 0f;
        poseBlend   = Mathf.MoveTowards(poseBlend, pose, weaponBlendSpeed * dt);
        if (animator != null)
            animator.SetFloat(WeaponBlendHash, poseBlend);
    }

    // ---- Detecção de acerto ----

    private static int FindActiveWindow(AttackData attack, float fromTime, float toTime)
    {
        if (attack.hitWindows == null)
            return -1;
        // Considera o intervalo percorrido desde o último frame, para não "pular" janelas curtas.
        for (int i = 0; i < attack.hitWindows.Length; i++)
        {
            Vector2 w = attack.hitWindows[i];
            if (toTime >= w.x && fromTime <= w.y)
                return i;
        }
        return -1;
    }

    private static bool IsNearAnyWindow(AttackData attack, float time)
    {
        if (attack.hitWindows == null)
            return false;
        foreach (Vector2 w in attack.hitWindows)
            if (time >= w.x - 0.06f && time <= w.y + 0.04f)
                return true;
        return false;
    }

    private void ResetSwing()
    {
        swingWindow = -1;
        for (int i = 0; i < blades.Length; i++)
        {
            blades[i].hasPrev    = false;
            blades[i].hasPrevTip = false;
        }
        hitThisWindow.Clear();
        if (weapon != null)
            weapon.SetTrail(false);
    }

    // Segmentos que acertam neste frame: a lâmina (e a segunda adaga) ou o punho/pé.
    private void UpdateBlades()
    {
        blades[1].valid = false;
        if (weapon != null)
        {
            SetBlade(ref blades[0], weapon.BaseWorld, weapon.TipWorld, weapon.bladeRadius);
            if (weapon.offhand != null && weapon.offhand != weapon && weapon.offhand.gameObject.activeInHierarchy)
                SetBlade(ref blades[1], weapon.offhand.BaseWorld, weapon.offhand.TipWorld, weapon.offhand.bladeRadius);
            return;
        }

        // Sem arma: do pulso até um pouco à frente da mão/pé (o osso fica na articulação).
        Transform bone = animator != null ? animator.GetBoneTransform(currentAttack.hitBone) : null;
        if (bone == null)
        {
            Vector3 p = transform.position + transform.forward + Vector3.up * 1.5f;
            SetBlade(ref blades[0], p, p, unarmedHitRadius);
            return;
        }
        Vector3 along = bone.parent != null ? (bone.position - bone.parent.position).normalized : transform.forward;
        SetBlade(ref blades[0], bone.position, bone.position + along * 0.25f, unarmedHitRadius);
    }

    private static void SetBlade(ref Blade blade, Vector3 a, Vector3 b, float radius)
    {
        blade.valid  = true;
        blade.a      = a;
        blade.b      = b;
        blade.radius = radius;
    }

    private void Sweep(Vector3 fromA, Vector3 fromB, Vector3 toA, Vector3 toB, float radius)
    {
        // Interpola entre a pose do frame anterior e a atual: golpes rápidos não atravessam o alvo.
        float travel = Mathf.Max(Vector3.Distance(fromA, toA), Vector3.Distance(fromB, toB));
        int steps    = Mathf.Clamp(Mathf.CeilToInt(travel / Mathf.Max(0.05f, radius * 0.75f)), 1, 12);

        for (int s = 1; s <= steps; s++)
        {
            float t   = (float)s / steps;
            Vector3 a = Vector3.Lerp(fromA, toA, t);
            Vector3 b = Vector3.Lerp(fromB, toB, t);
            int count = Physics.OverlapCapsuleNonAlloc(a, b, radius, overlapBuffer, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                TryHit(overlapBuffer[i], a, b);
            if (state != ActionState.Attack)
                return;
        }
    }

    private void TryHit(Collider collider, Vector3 segmentA, Vector3 segmentB)
    {
        if (collider.transform.IsChildOf(transform))
            return;

        Health target = collider.GetComponentInParent<Health>();
        if (target == null || target == health || !target.CanBeHit || hitThisWindow.Contains(target))
            return;
        ApplyHit(target, ClosestPointOnSegment(segmentA, segmentB, collider.bounds.center), currentAttack.reaction, currentAttack.knockback);
    }

    // Hitbox generosa: durante a janela, também acerta quem estiver logo à frente dentro do
    // alcance da arma, mesmo que a lâmina/punho "passe raspando" pela cápsula do alvo.
    private void CheckHitArc(ref Blade blade)
    {
        WeaponMoveset set = CurrentMoveset;
        float range = set != null ? set.hitArcRange : 0f;
        if (range <= 0f)
            return;

        // Direção da ponta vista de cima. No começo de algumas janelas a arma ainda está
        // erguida atrás da cabeça: o acerto só vale quando a ponta cruza a direção do alvo.
        Vector3 tip = blade.b - transform.position;
        tip.y = 0f;
        bool tipValid = tip.magnitude >= hitArcMinTipDistance;
        bool hadPrevTip = blade.hasPrevTip;
        Vector3 previousTip = blade.prevTip;
        blade.prevTip    = tip;
        blade.hasPrevTip = tipValid;
        if (!tipValid)
            return;

        for (int i = Health.Active.Count - 1; i >= 0; i--)
        {
            Health target = Health.Active[i];
            if (target == null || target == health || !target.CanBeHit || hitThisWindow.Contains(target))
                continue;

            Vector3 toTarget = target.transform.position - transform.position;
            float height = toTarget.y;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance > range || distance < 0.01f || Vector3.Angle(transform.forward, toTarget) > hitArcAngle)
                continue;
            if (height > hitArcMaxHeight || height < -hitArcMaxHeight)
                continue;
            if (!TipCrossesTarget(previousTip, hadPrevTip, tip, toTarget))
                continue;

            // Faíscas no ponto da arma mais próximo do peito do alvo
            Vector3 chest = target.transform.position + Vector3.up * 1.4f;
            Vector3 onWeapon = ClosestPointOnSegment(blade.a, blade.b, chest);
            ApplyHit(target, Vector3.Lerp(onWeapon, chest - toTarget / distance * 0.4f, 0.5f), currentAttack.reaction, currentAttack.knockback);
            if (state != ActionState.Attack)
                return;
        }
    }

    // A ponta está perto da direção do alvo, ou passou por ela desde o último frame
    // (cortes rápidos andam dezenas de graus por frame).
    private bool TipCrossesTarget(Vector3 previousTip, bool hasPrevious, Vector3 tip, Vector3 toTarget)
    {
        float targetAngle = Vector3.SignedAngle(transform.forward, toTarget, Vector3.up);
        float tipAngle    = Vector3.SignedAngle(transform.forward, tip, Vector3.up);
        if (Mathf.Abs(Mathf.DeltaAngle(tipAngle, targetAngle)) <= hitArcTipAngle)
            return true;
        if (!hasPrevious)
            return false;

        float previousAngle = Vector3.SignedAngle(transform.forward, previousTip, Vector3.up);
        // Ignora passagens por trás do corpo (ângulos dando a volta em ±180°)
        if (Mathf.Abs(tipAngle - previousAngle) >= 180f)
            return false;
        return targetAngle >= Mathf.Min(previousAngle, tipAngle) && targetAngle <= Mathf.Max(previousAngle, tipAngle);
    }

    private void ApplyHit(Health target, Vector3 point, HitReaction reaction, float knockback)
    {
        hitThisWindow.Add(target);
        AttackData attack = currentAttack;

        Vector3 direction = target.transform.position - transform.position;
        direction.y = 0f;
        direction   = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

        IAirborneTarget air = target.GetComponent<IAirborneTarget>();
        bool targetInAir    = air != null && air.IsAirborne;
        bool heavy          = attack.heavy || reaction != HitReaction.Normal;
        var info = new DamageInfo
        {
            amount       = attack.damage * (weapon != null ? weapon.damageMultiplier : 1f),
            point        = point,
            direction    = direction,
            knockback    = knockback,
            heavy        = heavy,
            reaction     = reaction,
            launchHeight = attack.launchHeight,
            airborneHit  = targetInAir || !controller.IsGrounded,
            source       = gameObject
        };
        if (!target.TakeDamage(info))
            return;

        HitStop.Request(info.heavy ? heavyHitStopDuration : hitStopDuration);
        ThirdPersonCamera.Shake(info.heavy ? heavyCameraShake : cameraShake);
        HitEffects.Spawn(info.point, direction, info.heavy);
        HitLanded?.Invoke(info, target);
    }

    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab   = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-6f)
            return a;
        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSq);
        return a + ab * t;
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || state != ActionState.Attack || currentAttack == null)
            return;
        Gizmos.color = swingWindow >= 0 ? Color.red : new Color(1f, 1f, 1f, 0.3f);
        foreach (Blade blade in blades)
        {
            if (!blade.valid)
                continue;
            Gizmos.DrawWireSphere(blade.a, blade.radius);
            Gizmos.DrawWireSphere(blade.b, blade.radius);
            Gizmos.DrawLine(blade.a, blade.b);
        }
    }
}
