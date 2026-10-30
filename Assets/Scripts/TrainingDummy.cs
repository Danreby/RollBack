using System.Collections.Generic;
using UnityEngine;

// Boneco de treino: reage aos golpes de acordo com o lado do impacto, é empurrado,
// pisca ao ser atingido, cai quando a vida acaba e levanta sozinho alguns segundos depois.
// Também cede ao corpo do player: encostado, o passo dos golpes empurra o boneco para trás.
//
// Malabarismo (hack'n slash): o lançador joga o boneco para o alto; golpes no ar o mantêm
// flutuando (cada acerto dá um pequeno impulso e a gravidade fica fraca por um instante); o golpe
// de queda o crava no chão com um quique; finalizadores pesados o arremessam para trás. Caído, ele
// levanta com um kip-up. Reações da Universal Animation Library (Quaternius, CC0).
[RequireComponent(typeof(Health))]
public class TrainingDummy : MonoBehaviour, IPushable, IAirborneTarget
{
    [Header("Estados do Animator")]
    public string idleState      = "Idle";
    public string hitFrontState  = "HitFront";
    public string hitFront2State = "HitFront2";
    public string hitBackState   = "HitBack";
    public string hitLeftState   = "HitLeft";
    public string hitRightState  = "HitRight";
    public string knockdownState = "Knockdown";
    public string getUpState     = "GetUp";
    public string launchState    = "Launch";
    public string airIdleState   = "AirIdle";
    public string airHitLeftState  = "AirHitL";
    public string airHitRightState = "AirHitR";
    public string airFallState   = "AirFall";
    public string fallImpactState = "FallImpact";
    public string knockbackState = "KnockbackFly";
    public string kipUpState     = "KipUp";

    [Header("Reação")]
    [Tooltip("Duração do empurrão causado pelos golpes")]
    public float knockbackDuration = 0.25f;
    [Tooltip("Tempo atordoado depois de um golpe (não gira para o player)")]
    public float stunTime = 0.8f;

    [Header("No ar")]
    public float gravity = 25f;
    [Tooltip("Golpe no ar sem altura definida: quanto o boneco sobe")]
    public float airHitBump = 0.5f;
    [Tooltip("Depois de cada golpe no ar, a gravidade fica fraca por este tempo (o boneco 'flutua')")]
    public float airHitFloatTime = 0.45f;
    [Range(0f, 1f)] public float airHitFloatGravity = 0.12f;
    [Tooltip("Lançado: fica flutuando por este tempo no topo do voo (dá tempo de emendar o combo aéreo)")]
    public float launchHangTime = 0.35f;
    [Tooltip("Velocidade com que o golpe de queda crava o boneco no chão")]
    public float slamSpeed = 24f;
    [Tooltip("Quique ao ser cravado no chão")]
    public float slamBounce = 4.5f;
    [Tooltip("Tempo caído antes do kip-up")]
    public float downTime = 0.7f;
    [Tooltip("Duração do voo do arremesso (finalizador pesado)")]
    public float knockbackFlyTime = 0.55f;

    [Header("Queda e retorno")]
    public float respawnDelay = 3f;

    [Header("Comportamento")]
    public bool facePlayer = true;
    public float turnSpeed = 120f;
    [Tooltip("Altura da barra de vida, acima dos pés")]
    public float headHeight = 2.7f;

    [Header("Flash de dano")]
    public Color flashColor = new Color(1f, 0.55f, 0.35f);
    public float flashIntensity = 2.5f;
    public float flashDuration = 0.15f;

    public static readonly List<TrainingDummy> All = new List<TrainingDummy>();

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private enum Pose { Standing, Airborne, Down, GettingUp }

    private Health health;
    private Animator animator;
    private CharacterController controller;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private Transform player;

    private Pose pose;
    private Vector3 knockVelocity;
    private float knockTimeLeft;
    private float knockTotalTime;
    private float verticalSpeed;
    private float stunnedUntil;
    private float floatUntil;
    private float apexTime;
    private float launchHangUntil;
    private float airAnimTime;
    private bool alternateFront;
    private bool alternateAir;
    private bool spiked;
    private bool bounced;
    private bool deathKnockdown;
    private float poseTime;
    private float flashTime = -1f;
    private string airAnim;

    public Health Health => health;
    public bool IsDown => pose == Pose.Down || pose == Pose.GettingUp;
    public bool IsAirborne => pose == Pose.Airborne;
    public bool IsStanding => pose == Pose.Standing;
    // Atordoado logo depois de apanhar (usado pelo SparringAI para não atacar no meio da reação)
    public bool IsStunned => Time.time < stunnedUntil;

    // Toca um estado do Animator por fora (ataques do SparringAI, postura de guarda)
    public void PlayState(string state, float transition)
    {
        if (pose == Pose.Standing)
            Play(state, transition);
    }
    public Vector3 HeadPosition => transform.position + Vector3.up * headHeight;

    private void Awake()
    {
        health     = GetComponent<Health>();
        animator   = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        renderers  = GetComponentsInChildren<Renderer>();
        block      = new MaterialPropertyBlock();
        if (animator != null)
            animator.applyRootMotion = false;
    }

    private void OnEnable()
    {
        All.Add(this);
        health.Damaged += OnDamaged;
        health.Died    += OnDied;
    }

    private void OnDisable()
    {
        All.Remove(this);
        health.Damaged -= OnDamaged;
        health.Died    -= OnDied;
    }

    private void Start()
    {
        PlayerCombat playerCombat = FindFirstObjectByType<PlayerCombat>();
        if (playerCombat != null)
            player = playerCombat.transform;
    }

    private void Play(string state, float transition)
    {
        if (animator != null && !string.IsNullOrEmpty(state))
            animator.CrossFadeInFixedTime(state, transition, 0, 0f);
    }

    private void SetPose(Pose next)
    {
        pose     = next;
        poseTime = Time.time;
    }

    // Empurrão horizontal: velocidade inicial que, caindo linearmente até zero, percorre "distance" em "duration".
    private void Knock(Vector3 direction, float distance, float duration)
    {
        knockTotalTime = Mathf.Max(0.01f, duration);
        knockVelocity  = direction * (2f * distance / knockTotalTime);
        knockTimeLeft  = knockTotalTime;
    }

    private void OnDamaged(DamageInfo info)
    {
        flashTime    = 0f;
        stunnedUntil = Time.time + stunTime;

        if (info.reaction == HitReaction.Launch && pose != Pose.GettingUp)
        {
            Launch(info.launchHeight > 0f ? info.launchHeight : 3f);
            Knock(info.direction, info.knockback * 0.3f, knockbackDuration);
            return;
        }

        if (pose == Pose.Airborne)
        {
            if (info.reaction == HitReaction.Slam)
            {
                // Cravado no chão
                verticalSpeed = -slamSpeed;
                floatUntil    = 0f;
                spiked        = true;
                Knock(info.direction, info.knockback * 0.3f, knockbackDuration);
                PlayAir(airFallState, 0.05f);
                return;
            }
            // Malabarismo: cada golpe sustenta o boneco no ar por mais um instante
            // O impulso é calculado com a gravidade fraca da flutuação: o boneco sobe "bump" metros e
            // fica pairando, na mesma altura em que o player sobe com cada golpe aéreo
            float bump = info.launchHeight > 0f ? info.launchHeight : airHitBump;
            verticalSpeed = Mathf.Max(verticalSpeed, Mathf.Sqrt(2f * gravity * airHitFloatGravity * bump));
            floatUntil    = Time.time + airHitFloatTime;
            Knock(info.direction, info.knockback * (info.heavy ? 0.6f : 0.3f), knockbackDuration);
            alternateAir = !alternateAir;
            PlayAir(alternateAir ? airHitLeftState : airHitRightState, 0.04f);
            return;
        }

        if (pose == Pose.Down)
        {
            // No chão: o golpe conta (dano e flash), sem mudar a pose
            return;
        }
        if (pose == Pose.GettingUp)
            return;

        if (info.reaction == HitReaction.Slam)
        {
            // Onda de choque do golpe de queda: o boneco quica e cai
            Launch(0.6f);
            spiked = true;
            Knock(info.direction, Mathf.Max(info.knockback, 1f), 0.4f);
            PlayAir(airFallState, 0.05f);
            return;
        }

        if (info.reaction == HitReaction.Knockback && !health.IsDead)
        {
            // Arremessado para trás: voa e cai deitado
            Knock(info.direction, Mathf.Max(info.knockback, 2.5f), knockbackFlyTime);
            SetPose(Pose.Down);
            deathKnockdown = false;
            Play(knockbackState, 0.04f);
            return;
        }

        Knock(info.direction, info.knockback, knockbackDuration);
        if (health.IsDead || animator == null)
            return; // a queda é tratada em OnDied

        // De onde veio o golpe, no espaço local do boneco.
        Vector3 from = transform.InverseTransformDirection(-info.direction);
        string state;
        if (from.z >= 0.5f)
        {
            // Alterna entre duas reações de frente: golpes seguidos sempre reiniciam a animação.
            state          = alternateFront || info.heavy ? hitFront2State : hitFrontState;
            alternateFront = !alternateFront;
        }
        else if (from.z <= -0.5f)
            state = hitBackState;
        else
            state = from.x > 0f ? hitRightState : hitLeftState;

        Play(state, 0.04f);
    }

    private void Launch(float height)
    {
        SetPose(Pose.Airborne);
        verticalSpeed   = Mathf.Sqrt(2f * gravity * Mathf.Max(0.1f, height));
        floatUntil      = 0f;
        apexTime        = Time.time + verticalSpeed / gravity;
        launchHangUntil = apexTime + launchHangTime;
        spiked        = false;
        bounced       = false;
        PlayAir(launchState, 0.04f);
    }

    private void PlayAir(string state, float transition)
    {
        airAnim     = state;
        airAnimTime = Time.time;
        Play(state, transition);
    }

    public Vector3 Push(Vector3 delta)
    {
        delta.y = 0f;
        // No ar, caído (ou levantando) o boneco não desliza pelo chão
        if (pose != Pose.Standing || delta.sqrMagnitude < 1e-8f)
            return Vector3.zero;

        Vector3 before = transform.position;
        if (controller != null && controller.enabled)
            controller.Move(delta);
        else
            transform.position += delta;
        Vector3 moved = transform.position - before;
        moved.y = 0f;
        return moved;
    }

    private void OnDied(DamageInfo info)
    {
        deathKnockdown = true;
        if (pose == Pose.Airborne)
            return; // termina de cair e fica no chão até voltar
        SetPose(Pose.Down);
        Knock(info.direction, Mathf.Max(info.knockback, 0.8f), knockbackDuration);
        Play(info.reaction == HitReaction.Knockback ? knockbackState : knockdownState, 0.06f);
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        Vector3 motion = Vector3.zero;

        if (knockTimeLeft > 0f)
        {
            motion        += knockVelocity * (knockTimeLeft / knockTotalTime) * dt;
            knockTimeLeft -= dt;
        }

        if (pose == Pose.Airborne)
        {
            bool hanging = Time.time < floatUntil || (Time.time > apexTime && Time.time < launchHangUntil);
            float scale  = hanging ? airHitFloatGravity : 1f;
            verticalSpeed = Mathf.Max(verticalSpeed - gravity * scale * dt, -40f);
            // Começou a cair de verdade: pose de queda (deitado no ar)
            if (verticalSpeed < -5f && airAnim != airFallState)
                PlayAir(airFallState, 0.25f);
            else if (airAnim != airIdleState && airAnim != airFallState && Time.time - airAnimTime > 0.45f)
                PlayAir(airIdleState, 0.2f);
        }
        else if (controller != null && controller.enabled)
            verticalSpeed = controller.isGrounded ? -1f : Mathf.Max(verticalSpeed - gravity * dt, -40f);

        if (controller != null && controller.enabled)
            controller.Move(motion + Vector3.up * verticalSpeed * dt);
        else
            transform.position += motion + Vector3.up * verticalSpeed * dt;

        if (pose == Pose.Airborne && verticalSpeed <= 0f && Time.time - poseTime > 0.1f && IsOnGround())
            Land();

        UpdateRecovery();
        UpdateFacing(dt);
    }

    private bool IsOnGround()
    {
        return controller != null && controller.enabled ? controller.isGrounded : transform.position.y <= 0f;
    }

    private void Land()
    {
        ThirdPersonCamera.Shake(spiked ? 0.25f : 0.08f);
        if (spiked && !bounced)
        {
            // Quica uma vez depois de cravado no chão
            bounced       = true;
            spiked        = false;
            verticalSpeed = slamBounce;
            poseTime      = Time.time;
            HitEffects.Spawn(transform.position + Vector3.up * 0.2f, Vector3.up, false);
            PlayAir(airFallState, 0.05f);
            return;
        }
        verticalSpeed = -1f;
        SetPose(Pose.Down);
        Play(fallImpactState, 0.08f);
    }

    private void UpdateRecovery()
    {
        if (pose == Pose.Down)
        {
            float wait = deathKnockdown ? respawnDelay : downTime + 0.5f;
            if (Time.time - poseTime < wait)
                return;

            SetPose(Pose.GettingUp);
            if (deathKnockdown)
                health.ResetHealth();
            health.Invulnerable = true; // não apanha enquanto levanta
            Play(deathKnockdown && string.IsNullOrEmpty(kipUpState) ? getUpState : kipUpState, 0.15f);
            deathKnockdown = false;
            return;
        }

        if (pose == Pose.GettingUp)
        {
            bool finished = animator == null || Time.time - poseTime > 4f;
            if (!finished)
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                finished = !animator.IsInTransition(0) && (info.IsName(kipUpState) || info.IsName(getUpState))
                           && info.normalizedTime >= 0.9f;
            }
            if (finished)
            {
                SetPose(Pose.Standing);
                health.Invulnerable = false;
                Play(idleState, 0.25f);
            }
        }
    }

    private void UpdateFacing(float dt)
    {
        if (!facePlayer || player == null || pose != Pose.Standing || Time.time < stunnedUntil)
            return;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toPlayer), turnSpeed * dt);
    }

    private void LateUpdate()
    {
        if (flashTime < 0f)
            return;

        // Tempo real: o flash aparece mesmo durante o hit stop.
        flashTime += Time.unscaledDeltaTime;
        float strength = 1f - flashTime / flashDuration;
        if (strength <= 0f)
        {
            flashTime = -1f;
            foreach (Renderer r in renderers)
                r.SetPropertyBlock(null);
            return;
        }

        Color emission = flashColor * (flashIntensity * strength);
        foreach (Renderer r in renderers)
        {
            r.GetPropertyBlock(block);
            block.SetColor(EmissionColorId, emission);
            r.SetPropertyBlock(block);
        }
    }
}
