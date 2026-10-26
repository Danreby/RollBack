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
        bool rigReady = hand != null && upperArm != null && lowerArm != null && indexRoot != null && littleRoot != null;
        bool allowed  = offHandIK && weapon != null && rigReady
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
