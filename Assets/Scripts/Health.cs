using System;
using System.Collections.Generic;
using UnityEngine;

// Vida genérica, usada pelo player, pelos bonecos de treino e por futuros inimigos.
public class Health : MonoBehaviour, IDamageable
{
    public float maxHealth = 100f;

    // Todos os Health ativos na cena (usado pela mira assistida e pelo HUD).
    public static readonly List<Health> Active = new List<Health>();

    public float Current { get; private set; }
    public float Normalized => maxHealth > 0f ? Current / maxHealth : 0f;
    public bool IsDead => Current <= 0f;
    public float LastDamageTime { get; private set; } = -999f;

    // Ligado pela esquiva (frames de invencibilidade) ou enquanto o boneco levanta.
    public bool Invulnerable { get; set; }

    public bool CanBeHit => !IsDead && !Invulnerable && isActiveAndEnabled;

    public event Action<DamageInfo> Damaged;
    public event Action<DamageInfo> Died;
    public event Action Revived;

    private void Awake()
    {
        Current = maxHealth;
    }

    private void OnEnable()
    {
        if (!Active.Contains(this)) Active.Add(this);
    }

    private void OnDisable()
    {
        Active.Remove(this);
    }

    public bool TakeDamage(DamageInfo info)
    {
        if (!CanBeHit || info.amount <= 0f)
            return false;

        Current        = Mathf.Max(0f, Current - info.amount);
        LastDamageTime = Time.time;
        Damaged?.Invoke(info);

        if (Current <= 0f)
            Died?.Invoke(info);
        return true;
    }

    public void ResetHealth()
    {
        Current        = maxHealth;
        Invulnerable   = false;
        LastDamageTime = -999f;
        Revived?.Invoke();
    }
}
