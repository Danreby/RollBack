using System;
using UnityEngine;

// Mira travada (lock-on). E trava no inimigo mais perto do centro da tela; E de novo destrava.
// Com a mira travada:
//   - o personagem fica de frente para o alvo e anda de lado/para trás (strafe); disparar solta;
//   - a câmera enquadra o player e o alvo (ThirdPersonCamera);
//   - todo golpe mira o alvo travado (PlayerCombat);
//   - Tab, ou um puxão rápido do mouse para o lado, passa para o próximo inimigo naquela direção;
//   - o HUD de prismas girando (LockOnIndicator) marca o alvo.
// A mira solta sozinha se o alvo ficar longe demais ou escondido atrás do cenário por um tempo.
// Se o alvo cair (vida zerada), passa para o próximo inimigo por perto, ou solta.
[DefaultExecutionOrder(-60)]
public class TargetLock : MonoBehaviour
{
    [Tooltip("Distância máxima para travar")]
    public float lockRange = 18f;
    [Tooltip("Mais longe que isso a mira solta sozinha")]
    public float releaseRange = 26f;
    [Tooltip("Sem alvo na tela, trava no inimigo mais próximo até esta distância")]
    public float closeRange = 7f;
    [Tooltip("Tempo com o alvo escondido atrás do cenário até a mira soltar")]
    public float lostSightTime = 1.5f;
    [Tooltip("Puxão do mouse (soma do movimento em ~0,15 s) que troca de alvo. 0 desliga")]
    public float flickThreshold = 7f;
    public LayerMask sightMask = ~0;

    [Header("Referências")]
    public PlayerInputReader input;
    public ThirdPersonController controller;
    public Camera viewCamera;

    public event Action<Health> Locked;
    public event Action<Health> Switched;
    public event Action Released;

    public Health Target { get; private set; }
    public bool IsLocked => Target != null;
    // Ponto do alvo usado pela câmera e pelo HUD (meio do tronco)
    public Vector3 TargetPoint => Target != null ? AimPoint(Target) : transform.position;

    private Health self;
    private float hiddenTime;
    private float flickAccum;
    private float flickTime;
    private float flickCooldownUntil;
    private readonly RaycastHit[] sightHits = new RaycastHit[8];

    private void Awake()
    {
        self = GetComponent<Health>();
        if (input == null)
            input = GetComponent<PlayerInputReader>();
        if (controller == null)
            controller = GetComponent<ThirdPersonController>();
    }

    private Camera Cam => viewCamera != null ? viewCamera : Camera.main;

    private void Update()
    {
        if (input != null && input.LockOnPressed)
        {
            if (IsLocked)
                Release();
            else
                Acquire();
        }

        if (!IsLocked)
            return;

        if (IsValid(Target))
            hiddenTime = HasLineOfSight(Target) ? 0f : hiddenTime + Time.deltaTime;
        if (!IsValid(Target) || Distance(Target) > releaseRange || hiddenTime > lostSightTime)
        {
            // O alvo caiu (ou sumiu): tenta o próximo por perto
            Health next = Target != null && Target.IsDead ? FindBest(null, closeRange * 1.5f) : null;
            if (next != null)
                SetTarget(next, true);
            else
                Release();
            return;
        }

        if (input != null)
        {
            if (input.SwitchTargetPressed)
                SwitchTarget(1f);
            else
                UpdateFlick(input.Look.x);
        }
    }

    public void Acquire()
    {
        Health best = FindBest(null, lockRange);
        if (best != null)
            SetTarget(best, false);
        else
            ThirdPersonCamera.RecenterBehindTarget(); // sem alvo: a câmera volta para trás do personagem
    }

    public void Release()
    {
        if (Target == null)
            return;
        Target = null;
        if (controller != null)
            controller.LockTarget = null;
        Released?.Invoke();
    }

    // Passa para o inimigo mais próximo na tela para o lado indicado (+1 direita, -1 esquerda),
    // dando a volta quando não há ninguém daquele lado.
    public void SwitchTarget(float side)
    {
        Camera cam = Cam;
        if (cam == null || Target == null)
            return;
        float current = cam.WorldToViewportPoint(AimPoint(Target)).x;
        Health best = null, wrap = null;
        float bestDelta = float.MaxValue, wrapValue = 0f;
        foreach (Health h in Health.Active)
        {
            if (h == Target || !IsValid(h) || Distance(h) > lockRange)
                continue;
            Vector3 vp = cam.WorldToViewportPoint(AimPoint(h));
            if (vp.z <= 0f)
                continue;
            float delta = (vp.x - current) * Mathf.Sign(side);
            if (delta > 0f && delta < bestDelta)
            {
                bestDelta = delta;
                best      = h;
            }
            // Dar a volta: o mais afastado para o outro lado
            float value = -delta;
            if (wrap == null || value > wrapValue)
            {
                wrap      = h;
                wrapValue = value;
            }
        }
        Health next = best != null ? best : wrap;
        if (next != null)
            SetTarget(next, true);
    }

    private void UpdateFlick(float mouseX)
    {
        if (flickThreshold <= 0f || Time.time < flickCooldownUntil)
            return;
        if (Time.time - flickTime > 0.15f || Mathf.Sign(mouseX) != Mathf.Sign(flickAccum))
        {
            flickAccum = 0f;
            flickTime  = Time.time;
        }
        flickAccum += mouseX;
        if (Mathf.Abs(flickAccum) >= flickThreshold)
        {
            SwitchTarget(Mathf.Sign(flickAccum));
            flickAccum         = 0f;
            flickCooldownUntil = Time.time + 0.35f;
        }
    }

    private void SetTarget(Health target, bool switched)
    {
        Target     = target;
        hiddenTime = 0f;
        if (controller != null)
            controller.LockTarget = target.transform;
        if (switched)
            Switched?.Invoke(target);
        else
            Locked?.Invoke(target);
    }

    // Melhor alvo: perto do centro da tela e perto do player. Sem ninguém na tela, o mais próximo.
    private Health FindBest(Health exclude, float range)
    {
        Camera cam = Cam;
        Health best = null, nearest = null;
        float bestScore = float.MaxValue, nearestDistance = closeRange;
        foreach (Health h in Health.Active)
        {
            if (h == exclude || !IsValid(h))
                continue;
            float distance = Distance(h);
            if (distance > range)
                continue;
            if (distance < nearestDistance && HasLineOfSight(h))
            {
                nearestDistance = distance;
                nearest         = h;
            }
            if (cam == null)
                continue;
            Vector3 vp = cam.WorldToViewportPoint(AimPoint(h));
            if (vp.z <= 0f || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f)
                continue;
            if (!HasLineOfSight(h))
                continue;
            float fromCenter = new Vector2(vp.x - 0.5f, vp.y - 0.5f).magnitude;
            float score = fromCenter * 2f + distance / range;
            if (score < bestScore)
            {
                bestScore = score;
                best      = h;
            }
        }
        return best != null ? best : nearest;
    }

    private bool IsValid(Health h)
    {
        return h != null && h != self && h.isActiveAndEnabled && !h.IsDead;
    }

    private float Distance(Health h)
    {
        Vector3 d = h.transform.position - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    private bool HasLineOfSight(Health h)
    {
        Camera cam = Cam;
        Vector3 from = cam != null ? cam.transform.position : transform.position + Vector3.up * 1.8f;
        Vector3 to   = AimPoint(h);
        Vector3 dir  = to - from;
        float length = dir.magnitude;
        if (length < 0.01f)
            return true;
        int count = Physics.RaycastNonAlloc(from, dir / length, sightHits, length, sightMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Transform t = sightHits[i].transform;
            // Personagens (o próprio player, o alvo, outros inimigos) e objetos soltos não bloqueiam
            if (t.IsChildOf(transform) || t.IsChildOf(h.transform) || t.GetComponentInParent<Health>() != null)
                continue;
            Rigidbody body = sightHits[i].rigidbody;
            if (body != null && !body.isKinematic)
                continue;
            return false;
        }
        return true;
    }

    public static Vector3 AimPoint(Health h)
    {
        if (h.TryGetComponent(out TrainingDummy dummy))
            return dummy.HeadPosition - Vector3.up * (dummy.headHeight * 0.4f);
        if (h.TryGetComponent(out CharacterController cc))
            return h.transform.TransformPoint(cc.center + Vector3.up * (cc.height * 0.15f));
        return h.transform.position + Vector3.up * 1.5f;
    }
}
