using System;
using UnityEngine;

// Recebe os Animation Events que já vêm dentro dos clipes da ExplosiveLLC
// (FootL, FootR, Hit, Land, WeaponSwitch). Sem um receptor no mesmo GameObject
// do Animator, a Unity enche o Console de avisos "AnimationEvent ... has no receiver".
// O dano NÃO depende destes eventos: as janelas de acerto ficam no PlayerCombat.
public class AnimationEventSink : MonoBehaviour
{
    public event Action Footstep;
    public event Action Landed;

    public void FootL() => Footstep?.Invoke();
    public void FootR() => Footstep?.Invoke();
    public void Land() => Landed?.Invoke();
    public void Hit() { }
    public void WeaponSwitch() { }
}
