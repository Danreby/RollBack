using System;
using UnityEngine;

// Recebe os Animation Events que já vêm dentro dos clipes da ExplosiveLLC
// (FootL, FootR, Hit, Land, WeaponSwitch). Sem um receptor no mesmo GameObject
// do Animator, a Unity enche o Console de avisos "AnimationEvent ... has no receiver".
// O dano NÃO depende destes eventos: as janelas de acerto ficam no PlayerCombat.
public class AnimationEventSink : MonoBehaviour
{
    [Tooltip("Intervalo mínimo entre dois passos: nos blend trees o mesmo evento chega de mais de um clipe")]
    public float minStepInterval = 0.15f;

    public event Action Footstep;
    public event Action Landed;

    // Enquanto o Motion Matching anima a locomoção, os passos vêm dos contatos reais dos pés
    // (ExternalStep); os eventos dos clipes do Animator (que não aparecem na tela) são ignorados.
    public bool ExternalFootsteps { get; set; }

    private float lastStepTime = -999f;

    public void FootL(AnimationEvent evt) => Step(evt);
    public void FootR(AnimationEvent evt) => Step(evt);
    public void Land() => Landed?.Invoke();
    public void Hit() { }
    public void WeaponSwitch() { }

    public void ExternalStep()
    {
        RaiseStep();
    }

    private void Step(AnimationEvent evt)
    {
        if (ExternalFootsteps)
            return;
        // Só o clipe dominante da mistura conta (evita passos de um clipe quase sem peso)
        if (evt != null && evt.animatorClipInfo.weight < 0.5f)
            return;
        RaiseStep();
    }

    private void RaiseStep()
    {
        if (Time.time - lastStepTime < minStepInterval)
            return;
        lastStepTime = Time.time;
        Footstep?.Invoke();
    }
}
