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

    private float lastStepTime = -999f;

    public void FootL(AnimationEvent evt) => Step(evt);
    public void FootR(AnimationEvent evt) => Step(evt);
    public void Land() => Landed?.Invoke();
    public void Hit() { }
    public void WeaponSwitch() { }

    private void Step(AnimationEvent evt)
    {
        // Só o clipe dominante da mistura conta (evita passos de um clipe quase sem peso)
        if (evt != null && evt.animatorClipInfo.weight < 0.5f)
            return;
        if (Time.time - lastStepTime < minStepInterval)
            return;
        lastStepTime = Time.time;
        Footstep?.Invoke();
    }
}
