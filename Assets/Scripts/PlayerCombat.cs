using System;
using System.Collections.Generic;
using UnityEngine;

// Combate do player: combos armado/desarmado, ataque em corrida, buffer de input, esquiva
// com invencibilidade, mira assistida e detecção de acerto.
//
// Os golpes são disparados por CrossFade a partir daqui (e não por triggers no Animator):
// o script é a única fonte da verdade sobre "em qual golpe estou", então lógica e animação
// nunca dessincronizam e não sobram triggers "presos" disparando golpes fora de hora.
//
// Espaçamento: os cortes da espada dão passos longos (até ~2 m). Com um alvo à frente, o
// passo é encurtado para o golpe terminar na distância ideal e, encostado, o resto do passo
// empurra o alvo; assim o personagem não fica "andando no lugar" com os pés deslizando.
[RequireComponent(typeof(ThirdPersonController))]
[DefaultExecutionOrder(-50)]
public class PlayerCombat : MonoBehaviour
{
    private enum ActionState { None, Attack, Dodge }

    [Header("Combos")]
    public AttackData[] armedCombo;
    public AttackData[] unarmedCombo;
    [Tooltip("Por quanto tempo um clique fica guardado esperando a hora de encadear o próximo golpe")]
    public float inputBufferTime = 0.35f;

    [Header("Ataque em corrida")]
    [Tooltip("Golpe que sai ao atacar correndo (segurando Shift): avança no embalo da corrida")]
    public AttackData sprintAttackArmed;
    public AttackData sprintAttackUnarmed;
    [Tooltip("Velocidade mínima (m/s) para o clique virar ataque em corrida")]
    public float sprintAttackMinSpeed = 6.5f;

    [Header("Distância de ataque")]
    [Tooltip("Se o alvo estiver mais longe que isso (centro a centro), o personagem avança até essa distância antes do golpe sair. Os cortes da espada já dão um passo à frente, por isso o valor é maior")]
    public float attackRangeArmed = 2.2f;
    [Tooltip("Mesma ideia, para os socos")]
    public float attackRangeUnarmed = 1.15f;
    [Tooltip("Velocidade máxima do avanço automático até o alvo (m/s)")]
    public float lungeSpeed = 7f;
    [Tooltip("Só avança sozinho até alvos mais perto que isso")]
    public float lungeMaxDistance = 4.5f;
    [Tooltip("Distância (centro a centro) em que o golpe com espada deve terminar: o passo da animação é encurtado para chegar nela em vez de atropelar o alvo")]
    public float idealDistanceArmed = 1.4f;
    [Tooltip("Mesma ideia, para os socos")]
    public float idealDistanceUnarmed = 1.0f;
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

    [Header("Mira assistida")]
    public float targetSearchRadius = 6f;
    [Range(0f, 180f)] public float targetSearchAngle = 75f;
    [Tooltip("Velocidade de giro (graus/s) para alinhar com o alvo no começo do golpe")]
    public float attackTurnSpeed = 1080f;

    [Header("Acerto")]
    public LayerMask hitMask = ~0;
    [Tooltip("Raio da área de acerto dos socos")]
    public float unarmedHitRadius = 0.42f;
    [Tooltip("Além da lâmina/punho, acerta quem estiver à frente dentro deste alcance durante a janela do golpe (hitbox generosa). 0 desliga")]
    public float hitArcRangeArmed = 2.1f;
    public float hitArcRangeUnarmed = 1.45f;
    [Range(0f, 180f)] public float hitArcAngle = 70f;
    [Tooltip("Vista de cima, a ponta da arma/punho precisa estar (ou ter acabado de passar) a menos deste ângulo da direção do alvo")]
    [Range(0f, 90f)] public float hitArcTipAngle = 40f;
    [Tooltip("Distância horizontal mínima da ponta até o corpo para valer (espada erguida acima da cabeça não conta)")]
    public float hitArcMinTipDistance = 0.4f;

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
    public event Action<bool> WeaponChanged;

    private static readonly int LocomotionHash  = Animator.StringToHash("Locomotion");
    private static readonly int ActionSpeedHash = Animator.StringToHash("ActionSpeed");
    private static readonly int WeaponBlendHash = Animator.StringToHash("WeaponBlend");

    private ThirdPersonController controller;
    private PlayerInputReader input;
    private Animator animator;
    private Health health;
    private MeleeWeapon weapon;

    private ActionState state;
    private AttackData currentAttack;
    private int comboIndex = -1;
    private float actionTime;
    private float actionDuration = 1f;
    private float lastNormalizedTime;
    private float attackBufferedUntil = -1f;
    private float dodgeBufferedUntil  = -1f;
    private float dodgeReadyTime;
    private Vector3 steerDirection;
    private Health attackTarget;
    private IPushable attackPushable;
    private float stepScale = 1f;
    private Vector3 carryMomentum;
    private float weaponBlend;

    // Quão rápido (m/s por segundo) o embalo herdado de um ataque em corrida se dissipa
    private const float momentumDecay = 35f;

    // Detecção de acerto
    private int swingWindow = -1;
    private bool hasPrevSegment;
    private Vector3 prevA, prevB;
    private bool hasPrevTip;
    private Vector3 prevTip;
    private readonly HashSet<Health> hitThisWindow = new HashSet<Health>();
    private readonly Collider[] overlapBuffer = new Collider[32];

    public PlayerInputReader Input => input;
    public MeleeWeapon Weapon => weapon;
    public bool IsArmed => weapon != null;
    public bool IsBusy => state != ActionState.None;
    public bool IsAttacking => state == ActionState.Attack;
    public bool IsDodging => state == ActionState.Dodge;
    public int ComboIndex => comboIndex;
    public AttackData CurrentAttack => currentAttack;
    public float NormalizedTime => actionDuration > 0f ? actionTime / actionDuration : 1f;
    // 0 = pose desarmada, 1 = armada (transição suave ao pegar/largar a arma)
    public float WeaponBlend => weaponBlend;
    // Alvo que o próximo golpe vai mirar (o HUD mostra um marcador nele)
    public Health PreviewTarget { get; private set; }

    private AttackData[] CurrentCombo => IsArmed ? armedCombo : unarmedCombo;
    private bool AttackBuffered => Time.time <= attackBufferedUntil;
    private bool DodgeBuffered => Time.time <= dodgeBufferedUntil;

    private void Awake()
    {
        controller = GetComponent<ThirdPersonController>();
        input      = GetComponent<PlayerInputReader>();
        animator   = GetComponent<Animator>();
        health     = GetComponent<Health>();
    }

    private void OnEnable()
    {
        controller.ActionMotionFilter = FilterActionMotion;
    }

    private void OnDisable()
    {
        if (state != ActionState.None)
            EndAction(0.1f);
        if (controller.ActionMotionFilter == (Func<Vector3, Vector3>)FilterActionMotion)
            controller.ActionMotionFilter = null;
    }

    // ---- Pedidos (input do jogador ou código/testes) ----

    public void RequestAttack()
    {
        attackBufferedUntil = Time.time + inputBufferTime;
    }

    public void RequestDodge()
    {
        dodgeBufferedUntil = Time.time + inputBufferTime * 0.6f;
    }

    public void EquipWeapon(MeleeWeapon newWeapon)
    {
        if (state != ActionState.None)
            EndAction(0.15f);
        weapon = newWeapon;
        WeaponChanged?.Invoke(weapon != null);
    }

    public MeleeWeapon UnequipWeapon()
    {
        if (state != ActionState.None)
            EndAction(0.15f);
        MeleeWeapon old = weapon;
        if (old != null)
            old.SetTrail(false);
        weapon = null;
        WeaponChanged?.Invoke(false);
        return old;
    }

    // ---- Loop ----

    private void Update()
    {
        if (input != null)
        {
            if (input.AttackPressed) RequestAttack();
            if (input.DodgePressed)  RequestDodge();
        }

        UpdateWeaponBlend(Time.deltaTime);
        PreviewTarget = state == ActionState.None ? FindTarget(DesiredDirection()) : attackTarget;

        switch (state)
        {
            case ActionState.None:
                if (DodgeBuffered && CanStartDodge())
                    StartDodge();
                else if (AttackBuffered && CanStartAttack())
                {
                    AttackData sprintAttack = SprintAttackAvailable();
                    if (sprintAttack != null)
                        StartAttack(sprintAttack, -1);
                    else
                        StartComboAttack(0);
                }
                break;
            case ActionState.Attack:
                UpdateAttack(Time.deltaTime);
                break;
            case ActionState.Dodge:
                UpdateDodge(Time.deltaTime);
                break;
        }
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
            weapon.SetTrail(IsNearAnyWindow(currentAttack, n));

        if (window != swingWindow)
        {
            swingWindow    = window;
            hasPrevSegment = false;
            hasPrevTip     = false;
            hitThisWindow.Clear();
            if (window >= 0)
                SwingStarted?.Invoke();
        }
        if (window < 0)
            return;

        GetHitSegment(out Vector3 a, out Vector3 b, out float radius);
        if (!hasPrevSegment)
        {
            prevA = a;
            prevB = b;
            hasPrevSegment = true;
        }
        Sweep(prevA, prevB, a, b, radius);
        prevA = a;
        prevB = b;
        if (state == ActionState.Attack)
            CheckHitArc(a, b);
    }

    // ---- Golpes ----

    private bool CanStartAttack()
    {
        AttackData[] combo = CurrentCombo;
        return controller.IsGrounded && combo != null && combo.Length > 0;
    }

    // Ataque em corrida: clique correndo (Shift) em velocidade alta.
    private AttackData SprintAttackAvailable()
    {
        AttackData attack = IsArmed ? sprintAttackArmed : sprintAttackUnarmed;
        if (attack == null || string.IsNullOrEmpty(attack.stateName) || !controller.IsSprinting)
            return null;
        Vector3 velocity = controller.Controller.velocity;
        velocity.y = 0f;
        return velocity.magnitude >= sprintAttackMinSpeed ? attack : null;
    }

    private void StartComboAttack(int index)
    {
        AttackData[] combo = CurrentCombo;
        if (combo == null || combo.Length == 0)
            return;
        index = Mathf.Clamp(index, 0, combo.Length - 1);
        StartAttack(combo[index], index);
    }

    // index: posição no combo (-1 = golpe fora do combo, como o ataque em corrida; o próximo clique abre o combo)
    private void StartAttack(AttackData attack, int index)
    {
        if (attack == null || string.IsNullOrEmpty(attack.stateName))
            return;

        // O embalo de um ataque em corrida não some de uma vez ao encadear o próximo golpe.
        carryMomentum = state == ActionState.Attack ? transform.forward * CarrySpeed() : Vector3.zero;

        ResetSwing();
        state               = ActionState.Attack;
        comboIndex          = index;
        currentAttack       = attack;
        actionTime          = 0f;
        lastNormalizedTime  = 0f;
        actionDuration      = attack.Duration;
        attackBufferedUntil = -1f;
        stepScale           = 1f;
        if (health != null)
            health.Invulnerable = false;

        // Mira: o alvo mais próximo na direção do input (ou da frente do personagem).
        steerDirection = DesiredDirection();
        attackTarget   = FindTarget(steerDirection);
        attackPushable = attackTarget != null ? attackTarget.GetComponent<IPushable>() : null;
        UpdateSteerDirection();

        controller.SetActionLock(true, attack.rootMotionScale);
        if (animator != null)
        {
            animator.SetFloat(ActionSpeedHash, attack.speed);
            animator.CrossFadeInFixedTime(attack.stateName, attack.transitionDuration, 0, 0f);
        }
        AttackStarted?.Invoke(attack, index);
    }

    private void UpdateAttack(float dt)
    {
        actionTime += dt;
        float n = NormalizedTime;
        AttackData attack = currentAttack;
        carryMomentum = Vector3.MoveTowards(carryMomentum, Vector3.zero, momentumDecay * dt);

        // Alinha com o alvo só antes do golpe sair; depois a direção fica travada.
        float steerEnd = attack.hitWindows != null && attack.hitWindows.Length > 0 ? attack.hitWindows[0].x : 0.25f;
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
        float range    = IsArmed ? attackRangeArmed : attackRangeUnarmed;
        float distance = DistanceToTarget(out Vector3 toTarget);
        stepScale = ComputeStepScale(attack, n, distance);
        if (attack.carrySpeed <= 0f && n < steerEnd && distance > range && distance < lungeMaxDistance)
            controller.Nudge(toTarget * Mathf.Min(lungeSpeed * dt, distance - range));

        AttackData[] combo = CurrentCombo;
        bool isLast = comboIndex >= combo.Length - 1;

        if (AttackBuffered)
        {
            if (!isLast && n >= attack.comboTime)
            {
                StartComboAttack(comboIndex + 1);
                return;
            }
            if (isLast && n >= attack.recoveryTime)
            {
                StartComboAttack(0);
                return;
            }
        }

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
            EndAction(0.25f);
    }

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
        return controller.HasMoveInput ? controller.MoveDirection.normalized : transform.forward;
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
        float room      = distance - (IsArmed ? idealDistanceArmed : idealDistanceUnarmed);
        return Mathf.Clamp(room / Mathf.Max(0.01f, remaining), minStepScale, 1f);
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

    private Health FindTarget(Vector3 direction)
    {
        Health best     = null;
        float bestScore = float.MaxValue;

        foreach (Health candidate in Health.Active)
        {
            if (candidate == null || candidate == health || candidate.IsDead)
                continue;

            Vector3 toTarget = candidate.transform.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance > targetSearchRadius || distance < 0.01f)
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

    // ---- Esquiva ----

    private bool CanStartDodge()
    {
        AnimationClip clip = IsArmed ? dodgeClipArmed : dodgeClipUnarmed;
        return clip != null && controller.IsGrounded && Time.time >= dodgeReadyTime;
    }

    private void StartDodge()
    {
        AnimationClip clip = IsArmed ? dodgeClipArmed : dodgeClipUnarmed;
        string stateName   = IsArmed ? dodgeStateArmed : dodgeStateUnarmed;

        ResetSwing();
        controller.SnapFacing(DesiredDirection());
        controller.ClearFacing();

        state               = ActionState.Dodge;
        currentAttack       = null;
        attackTarget        = null;
        attackPushable      = null;
        carryMomentum       = Vector3.zero;
        comboIndex          = -1;
        actionTime          = 0f;
        actionDuration      = clip.length / Mathf.Max(0.05f, dodgeSpeed);
        dodgeBufferedUntil  = -1f;
        attackBufferedUntil = -1f;
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
            if (AttackBuffered && CanStartAttack())
            {
                StartComboAttack(0);
                return;
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

    // ---- Comum ----

    private void EndAction(float transition)
    {
        // Saindo de um golpe com embalo (ataque em corrida): continua na mesma velocidade em vez de parar seco.
        Vector3 momentum = transform.forward * CarrySpeed() + carryMomentum;

        ResetSwing();
        state          = ActionState.None;
        currentAttack  = null;
        attackTarget   = null;
        attackPushable = null;
        carryMomentum  = Vector3.zero;
        comboIndex     = -1;
        if (health != null)
            health.Invulnerable = false;

        controller.SetActionLock(false);
        if (momentum.sqrMagnitude > 0.01f)
            controller.SetMomentum(momentum);
        if (animator != null && isActiveAndEnabled)
            animator.CrossFadeInFixedTime(LocomotionHash, transition, 0);
    }

    private void UpdateWeaponBlend(float dt)
    {
        weaponBlend = Mathf.MoveTowards(weaponBlend, IsArmed ? 1f : 0f, weaponBlendSpeed * dt);
        if (animator != null)
            animator.SetFloat(WeaponBlendHash, weaponBlend);
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
        swingWindow    = -1;
        hasPrevSegment = false;
        hasPrevTip     = false;
        hitThisWindow.Clear();
        if (weapon != null)
            weapon.SetTrail(false);
    }

    private void GetHitSegment(out Vector3 a, out Vector3 b, out float radius)
    {
        if (weapon != null)
        {
            a      = weapon.BaseWorld;
            b      = weapon.TipWorld;
            radius = weapon.bladeRadius;
            return;
        }

        // Sem arma: do pulso até um pouco à frente da mão (o osso da mão fica no pulso).
        Transform bone = animator != null ? animator.GetBoneTransform(currentAttack.hitBone) : null;
        if (bone == null)
        {
            a = b  = transform.position + transform.forward + Vector3.up * 1.5f;
            radius = unarmedHitRadius;
            return;
        }
        Vector3 along = bone.parent != null ? (bone.position - bone.parent.position).normalized : transform.forward;
        a      = bone.position;
        b      = bone.position + along * 0.25f;
        radius = unarmedHitRadius;
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
        ApplyHit(target, ClosestPointOnSegment(segmentA, segmentB, collider.bounds.center));
    }

    // Hitbox generosa: durante a janela, também acerta quem estiver logo à frente dentro do
    // alcance da arma, mesmo que a lâmina/punho "passe raspando" pela cápsula do alvo.
    private void CheckHitArc(Vector3 segmentA, Vector3 segmentB)
    {
        float range = IsArmed ? hitArcRangeArmed : hitArcRangeUnarmed;
        if (range <= 0f)
            return;

        // Direção da ponta vista de cima. No começo de algumas janelas a espada ainda está
        // erguida atrás da cabeça: o acerto só vale quando a ponta cruza a direção do alvo.
        Vector3 tip = segmentB - transform.position;
        tip.y = 0f;
        bool tipValid = tip.magnitude >= hitArcMinTipDistance;
        bool hadPrevTip = hasPrevTip;
        Vector3 previousTip = prevTip;
        prevTip    = tip;
        hasPrevTip = tipValid;
        if (!tipValid)
            return;

        for (int i = Health.Active.Count - 1; i >= 0; i--)
        {
            Health target = Health.Active[i];
            if (target == null || target == health || !target.CanBeHit || hitThisWindow.Contains(target))
                continue;

            Vector3 toTarget = target.transform.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance > range || distance < 0.01f || Vector3.Angle(transform.forward, toTarget) > hitArcAngle)
                continue;
            if (!TipCrossesTarget(previousTip, hadPrevTip, tip, toTarget))
                continue;

            // Faíscas no ponto da arma mais próximo do peito do alvo
            Vector3 chest = target.transform.position + Vector3.up * 1.4f;
            Vector3 onWeapon = ClosestPointOnSegment(segmentA, segmentB, chest);
            ApplyHit(target, Vector3.Lerp(onWeapon, chest - toTarget / distance * 0.4f, 0.5f));
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

    private void ApplyHit(Health target, Vector3 point)
    {
        hitThisWindow.Add(target);

        Vector3 direction = target.transform.position - transform.position;
        direction.y = 0f;
        direction   = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

        var info = new DamageInfo
        {
            amount    = currentAttack.damage * (weapon != null ? weapon.damageMultiplier : 1f),
            point     = point,
            direction = direction,
            knockback = currentAttack.knockback,
            heavy     = currentAttack.heavy,
            source    = gameObject
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
        GetHitSegment(out Vector3 a, out Vector3 b, out float radius);
        Gizmos.color = swingWindow >= 0 ? Color.red : new Color(1f, 1f, 1f, 0.3f);
        Gizmos.DrawWireSphere(a, radius);
        Gizmos.DrawWireSphere(b, radius);
        Gizmos.DrawLine(a, b);
    }
}
