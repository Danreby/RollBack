using System.Collections.Generic;
using UnityEngine;

// Boneco de treino: reage aos golpes de acordo com o lado do impacto, é empurrado,
// pisca ao ser atingido, cai quando a vida acaba e levanta sozinho alguns segundos depois.
// Também cede ao corpo do player: encostado, o passo dos golpes empurra o boneco para trás.
[RequireComponent(typeof(Health))]
public class TrainingDummy : MonoBehaviour, IPushable
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

    [Header("Reação")]
    [Tooltip("Duração do empurrão causado pelos golpes")]
    public float knockbackDuration = 0.25f;
    [Tooltip("Tempo atordoado depois de um golpe (não gira para o player)")]
    public float stunTime = 0.8f;

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

    private Health health;
    private Animator animator;
    private CharacterController controller;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private Transform player;

    private Vector3 knockVelocity;
    private float knockTimeLeft;
    private float verticalSpeed;
    private float stunnedUntil;
    private bool alternateFront;
    private bool down;
    private bool gettingUp;
    private float downTime;
    private float getUpStartTime;
    private float flashTime = -1f;

    public Health Health => health;
    public bool IsDown => down;
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

    private void OnDamaged(DamageInfo info)
    {
        flashTime = 0f;

        // Velocidade inicial que, caindo linearmente até zero, percorre exatamente "knockback" metros.
        knockVelocity = info.direction * (2f * info.knockback / Mathf.Max(0.01f, knockbackDuration));
        knockTimeLeft = knockbackDuration;
        stunnedUntil  = Time.time + stunTime;

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

        animator.CrossFadeInFixedTime(state, 0.04f, 0, 0f);
    }

    public Vector3 Push(Vector3 delta)
    {
        delta.y = 0f;
        // Caído (ou levantando) o boneco não desliza pelo chão
        if (down || delta.sqrMagnitude < 1e-8f)
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
        down      = true;
        gettingUp = false;
        downTime  = Time.time;
        knockVelocity = info.direction * (2f * Mathf.Max(info.knockback, 0.8f) / Mathf.Max(0.01f, knockbackDuration));
        if (animator != null)
            animator.CrossFadeInFixedTime(knockdownState, 0.06f, 0, 0f);
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        Vector3 motion = Vector3.zero;

        if (knockTimeLeft > 0f)
        {
            motion        += knockVelocity * (knockTimeLeft / knockbackDuration) * dt;
            knockTimeLeft -= dt;
        }

        if (controller != null && controller.enabled)
        {
            verticalSpeed = controller.isGrounded ? -1f : verticalSpeed - 20f * dt;
            controller.Move(motion + Vector3.up * verticalSpeed * dt);
        }
        else
            transform.position += motion;

        UpdateRecovery();
        UpdateFacing(dt);
    }

    private void UpdateRecovery()
    {
        if (!down)
            return;

        if (!gettingUp && Time.time - downTime >= respawnDelay)
        {
            gettingUp      = true;
            getUpStartTime = Time.time;
            health.ResetHealth();
            health.Invulnerable = true; // não apanha enquanto levanta
            if (animator != null)
                animator.CrossFadeInFixedTime(getUpState, 0.15f, 0, 0f);
            return;
        }

        if (gettingUp)
        {
            bool finished = animator == null || Time.time - getUpStartTime > 6f;
            if (!finished)
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                finished = !animator.IsInTransition(0) && info.IsName(getUpState) && info.normalizedTime >= 0.92f;
            }
            if (finished)
            {
                down      = false;
                gettingUp = false;
                health.Invulnerable = false;
                if (animator != null)
                    animator.CrossFadeInFixedTime(idleState, 0.25f, 0, 0f);
            }
        }
    }

    private void UpdateFacing(float dt)
    {
        if (!facePlayer || player == null || down || Time.time < stunnedUntil)
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
