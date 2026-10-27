using UnityEngine;

// Pegada da arma nas mãos do player.
//
// Mão direita: a camada "RightHandGrip" do Animator (máscara só com os dedos da mão direita,
// pose da espada da ExplosiveLLC) fecha a mão em volta do cabo enquanto houver arma. Os clipes
// de espada da EEJANAI gravam os dedos com valores fixos e fora da faixa (mão meio aberta,
// dedos cruzados), por isso a espada parecia solta na mão durante os golpes.
//
// Mão esquerda: nas animações de duas mãos ela não cai no lugar certo do cabo desta espada.
// Na corrida/pulo da ExplosiveLLC ela segura um ponto baixo, pensado para a espada longa do pacote
// (ficava no ar abaixo do pomo); na postura de batalha da EEJANAI ela fica em cima da guarda.
// Depois da animação o braço esquerdo é ajustado (IK de dois ossos) para o punho deslizar ao
// longo do cabo até MeleeWeapon.offHandGrip, logo abaixo da mão direita. Só age quando o punho
// já está sobre a linha do cabo: golpes de uma mão e a mão solta no rolamento não são afetados.
// Armas de uma mão (MeleeWeapon.allowOffHandGrip desligado) não puxam a mão esquerda.
//
// Segunda lâmina (adagas duplas): a mão esquerda copia, espelhada, a pegada dos dedos da direita
// (o esqueleto do RPG-Character é simétrico: a rotação local espelhada no plano XY da mão é
// (-x, -y, z, w)), então ela fecha em volta do cabo da segunda adaga.
[DefaultExecutionOrder(50)]
public class WeaponGripIK : MonoBehaviour
{
    [Tooltip("Camada do Animator que fecha os dedos da mão direita (o peso acompanha a troca para a pose armada)")]
    public string gripLayerName = "RightHandGrip";

    [Header("Mão esquerda")]
    public bool offHandIK = true;
    [Tooltip("O punho esquerdo só é encaixado se já estiver a menos disto da linha do cabo (pose de duas mãos)")]
    public float attachRadius = 0.12f;
    [Tooltip("Distância máxima, ao longo do cabo, que o punho pode ser deslizado")]
    public float maxSlide = 0.5f;
    [Tooltip("Velocidade com que o ajuste liga/desliga ao entrar/sair de golpes de uma mão")]
    public float blendSpeed = 10f;

    private Animator animator;
    private PlayerCombat combat;
    private int gripLayer = -1;
    private float actionWeight;
    private Transform upperArm, lowerArm, hand, indexRoot, littleRoot;
    private Transform[] rightFingers, leftFingers;
    private float mirrorWeight;

    private static readonly HumanBodyBones[] FingerPairs =
    {
        HumanBodyBones.RightThumbProximal, HumanBodyBones.LeftThumbProximal,
        HumanBodyBones.RightThumbIntermediate, HumanBodyBones.LeftThumbIntermediate,
        HumanBodyBones.RightThumbDistal, HumanBodyBones.LeftThumbDistal,
        HumanBodyBones.RightIndexProximal, HumanBodyBones.LeftIndexProximal,
        HumanBodyBones.RightIndexIntermediate, HumanBodyBones.LeftIndexIntermediate,
        HumanBodyBones.RightIndexDistal, HumanBodyBones.LeftIndexDistal,
        HumanBodyBones.RightMiddleProximal, HumanBodyBones.LeftMiddleProximal,
        HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.LeftMiddleIntermediate,
        HumanBodyBones.RightMiddleDistal, HumanBodyBones.LeftMiddleDistal,
        HumanBodyBones.RightRingProximal, HumanBodyBones.LeftRingProximal,
        HumanBodyBones.RightRingIntermediate, HumanBodyBones.LeftRingIntermediate,
        HumanBodyBones.RightRingDistal, HumanBodyBones.LeftRingDistal,
        HumanBodyBones.RightLittleProximal, HumanBodyBones.LeftLittleProximal,
        HumanBodyBones.RightLittleIntermediate, HumanBodyBones.LeftLittleIntermediate,
        HumanBodyBones.RightLittleDistal, HumanBodyBones.LeftLittleDistal,
    };

    private void Awake()
    {
        animator = GetComponent<Animator>();
        combat   = GetComponent<PlayerCombat>();
        if (animator == null || !animator.isHuman)
            return;

        gripLayer  = animator.GetLayerIndex(gripLayerName);
        upperArm   = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        lowerArm   = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        hand       = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        indexRoot  = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
        littleRoot = animator.GetBoneTransform(HumanBodyBones.LeftLittleProximal);

        int pairs    = FingerPairs.Length / 2;
        rightFingers = new Transform[pairs];
        leftFingers  = new Transform[pairs];
        for (int i = 0; i < pairs; i++)
        {
            rightFingers[i] = animator.GetBoneTransform(FingerPairs[2 * i]);
            leftFingers[i]  = animator.GetBoneTransform(FingerPairs[2 * i + 1]);
        }
    }

    private void Update()
    {
        // Roda antes da avaliação do Animator neste frame
        if (gripLayer >= 0 && combat != null)
            animator.SetLayerWeight(gripLayer, combat.WeaponBlend);
    }

    private void LateUpdate()
    {
        MeleeWeapon weapon = combat != null ? combat.Weapon : null;
        MirrorFingers(weapon);

        bool rigReady = hand != null && upperArm != null && lowerArm != null && indexRoot != null && littleRoot != null;
        bool allowed  = offHandIK && weapon != null && weapon.allowOffHandGrip && weapon.offhand == null && rigReady
                        && (!combat.IsAttacking || (combat.CurrentAttack != null && combat.CurrentAttack.twoHanded));
        actionWeight = Mathf.MoveTowards(actionWeight, allowed ? 1f : 0f, blendSpeed * Time.deltaTime);
        if (actionWeight <= 0f || weapon == null || !rigReady)
            return;

        // Onde o punho esquerdo (meio dos nós dos dedos) está em relação ao cabo
        Vector3 axis   = weapon.GripAxisWorld;
        Vector3 offset = (indexRoot.position + littleRoot.position) * 0.5f - weapon.OffHandGripWorld;
        float along    = Vector3.Dot(offset, axis);
        float radial   = (offset - axis * along).magnitude;

        // Encaixe gradual: some se o punho sai da linha do cabo ou do trecho que dá para deslizar
        float weight = actionWeight
                       * (1f - Mathf.InverseLerp(attachRadius, attachRadius * 1.6f, radial))
                       * (1f - Mathf.InverseLerp(maxSlide, maxSlide * 1.3f, Mathf.Abs(along)));
        if (weight <= 0.001f || Mathf.Abs(along) < 0.001f)
            return;

        Quaternion handRotation = hand.rotation;
        SolveTwoBone(upperArm, lowerArm, hand, hand.position - axis * (along * weight));
        // A mão mantém a orientação da animação: o punho só desliza pelo cabo
        hand.rotation = handRotation;
    }

    // Dedos da mão esquerda = dedos da direita espelhados, enquanto houver segunda lâmina
    private void MirrorFingers(MeleeWeapon weapon)
    {
        if (rightFingers == null)
            return;
        bool dual = weapon != null && weapon.offhand != null && weapon.offhand != weapon;
        mirrorWeight = Mathf.MoveTowards(mirrorWeight, dual ? combat.WeaponBlend : 0f, blendSpeed * Time.deltaTime);
        if (mirrorWeight <= 0f)
            return;
        for (int i = 0; i < rightFingers.Length; i++)
        {
            Transform r = rightFingers[i], l = leftFingers[i];
            if (r == null || l == null)
                continue;
            Quaternion q = r.localRotation;
            l.localRotation = Quaternion.Slerp(l.localRotation, new Quaternion(-q.x, -q.y, q.z, q.w), mirrorWeight);
        }
    }

    // IK analítico de dois ossos: ajusta o cotovelo para a distância ombro-alvo e gira o braço até o alvo.
    private static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target)
    {
        Vector3 a = upper.position, b = lower.position, c = end.position;
        float lab = Vector3.Distance(a, b);
        float lbc = Vector3.Distance(b, c);
        if (lab < 1e-4f || lbc < 1e-4f)
            return;
        float lat = Mathf.Clamp(Vector3.Distance(a, target), Mathf.Abs(lab - lbc) + 1e-4f, lab + lbc - 1e-4f);

        // Cotovelo: abre/fecha no plano atual do braço
        Vector3 ba = a - b, bc = c - b;
        Vector3 bendAxis = Vector3.Cross(ba, bc);
        if (bendAxis.sqrMagnitude > 1e-10f)
        {
            float current = Vector3.Angle(ba, bc);
            float desired = Mathf.Acos(Mathf.Clamp((lab * lab + lbc * lbc - lat * lat) / (2f * lab * lbc), -1f, 1f)) * Mathf.Rad2Deg;
            lower.rotation = Quaternion.AngleAxis(desired - current, bendAxis.normalized) * lower.rotation;
        }

        // Ombro: aponta o braço (já com o cotovelo no ângulo certo) para o alvo
        upper.rotation = Quaternion.FromToRotation(end.position - a, target - a) * upper.rotation;
    }
}
