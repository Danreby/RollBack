using System.Collections.Generic;
using UnityEngine;

// Modelo visível do jogador (Unity-chan) copiando a pose do esqueleto de animação.
//
// O esqueleto do RPG-Character continua sendo o "motor" do personagem: o Motion Matching foi
// treinado para ele, e os golpes, o IK da pegada e a detecção de acerto (as armas ficam presas nas
// mãos dele) também dependem dele. Ele fica invisível e, a cada frame, depois de todos os ajustes
// de pose, a pose humanoide (músculos do Mecanim) é copiada para este modelo. Como as proporções
// são diferentes (a Unity-chan tem braços e cabeça de anime), depois da cópia braços e pernas são
// ajustados por IK de dois ossos para as mãos e os pés caírem exatamente onde estão os do
// esqueleto: a arma fica na mão e o pé apoiado fica no chão.
//
// A escala deste objeto deve igualar a "humanScale" dos dois avatares (altura do quadril), assim
// as passadas têm o mesmo tamanho e os pés não deslizam.
[DefaultExecutionOrder(80)] // depois do WeaponGripIK (50) e da inclinação do tronco; antes das molas do cabelo (100)
public class HumanoidRetarget : MonoBehaviour
{
    [Tooltip("Animator do esqueleto de animação (o RPG-Character invisível)")]
    public Animator source;
    [Tooltip("Ajusta os braços por IK para as mãos coincidirem com as do esqueleto (armas na mão)")]
    public bool matchHands = true;
    [Tooltip("Ajusta as pernas por IK para os pés coincidirem com os do esqueleto (pés no chão)")]
    public bool matchFeet = true;
    [Tooltip("Esconde as malhas do esqueleto de animação (as armas continuam visíveis)")]
    public bool hideSource = true;

    private struct Limb
    {
        public Transform upper, lower, end, sourceEnd;
        public Quaternion endOffset;
        public Vector3 bendFallback;
    }

    private Animator target;
    private HumanPoseHandler sourceHandler;
    private HumanPoseHandler targetHandler;
    private HumanPose pose;
    private Limb[] limbs;
    private Transform targetHips, sourceHips;
    private bool offsetsReady;
    private readonly List<Renderer> hiddenRenderers = new List<Renderer>();

    public Animator Target => target;

    private void Awake()
    {
        target = GetComponent<Animator>();
        if (source == null)
            source = transform.parent != null ? transform.parent.GetComponentInParent<Animator>() : null;
        if (source == null || target == null || !source.isHuman || !target.isHuman || source.avatar == null || target.avatar == null)
        {
            Debug.LogError("HumanoidRetarget: precisa de dois Animators humanoides (esqueleto de animação e modelo visível).", this);
            enabled = false;
            return;
        }

        limbs = new[]
        {
            MakeLimb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, Vector3.back),
            MakeLimb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, Vector3.back),
            MakeLimb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, Vector3.forward),
            MakeLimb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, Vector3.forward),
        };

        targetHips = target.GetBoneTransform(HumanBodyBones.Hips);
        sourceHips = source.GetBoneTransform(HumanBodyBones.Hips);

        // Este Animator só fornece o avatar e o mapeamento dos ossos: quem anima é o esqueleto de origem.
        target.enabled = false;
        sourceHandler = new HumanPoseHandler(source.avatar, source.transform);
        targetHandler = new HumanPoseHandler(target.avatar, transform);
        pose = new HumanPose();
    }

    private Limb MakeLimb(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end, Vector3 bendFallback)
    {
        return new Limb
        {
            upper        = target.GetBoneTransform(upper),
            lower        = target.GetBoneTransform(lower),
            end          = target.GetBoneTransform(end),
            sourceEnd    = source.GetBoneTransform(end),
            bendFallback = bendFallback,
        };
    }

    private void Start()
    {
        if (!hideSource)
            return;
        // Só as malhas do próprio esqueleto (as armas presas nele são MeshRenderers e continuam visíveis)
        foreach (SkinnedMeshRenderer smr in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.transform.IsChildOf(transform))
                continue;
            smr.enabled = false;
            hiddenRenderers.Add(smr);
        }
    }

    private void OnDisable()
    {
        foreach (Renderer r in hiddenRenderers)
            if (r != null)
                r.enabled = true;
        hiddenRenderers.Clear();
    }

    private void OnDestroy()
    {
        sourceHandler?.Dispose();
        targetHandler?.Dispose();
    }

    private void LateUpdate()
    {
        sourceHandler.GetHumanPose(ref pose);

        // O corpo (centro de massa) vem no espaço do pai da raiz de origem e é aplicado no espaço do pai
        // deste modelo, que é o próprio esqueleto de origem: converte passando pelo mundo.
        Transform sourceParent = source.transform.parent;
        Transform targetParent = transform.parent;
        Vector3 bodyPosition    = sourceParent != null ? sourceParent.TransformPoint(pose.bodyPosition) : pose.bodyPosition;
        Quaternion bodyRotation = sourceParent != null ? sourceParent.rotation * pose.bodyRotation : pose.bodyRotation;
        pose.bodyPosition = targetParent != null ? targetParent.InverseTransformPoint(bodyPosition) : bodyPosition;
        pose.bodyRotation = targetParent != null ? Quaternion.Inverse(targetParent.rotation) * bodyRotation : bodyRotation;
        targetHandler.SetHumanPose(ref pose);

        // O quadril fica exatamente sobre o do esqueleto (as alturas de quadril são iguais pela escala):
        // o modelo nunca se descola da cápsula, das armas nem dos pés.
        if (targetHips != null && sourceHips != null)
            targetHips.position = sourceHips.position;

        if (!offsetsReady)
        {
            // Mesma pose nos dois esqueletos: a diferença de rotação das mãos/pés é só a convenção
            // de eixos de cada modelo, constante. Guardada uma vez, deixa copiar a rotação depois.
            for (int i = 0; i < limbs.Length; i++)
                if (limbs[i].end != null && limbs[i].sourceEnd != null)
                    limbs[i].endOffset = Quaternion.Inverse(limbs[i].sourceEnd.rotation) * limbs[i].end.rotation;
            offsetsReady = true;
        }

        for (int i = 0; i < limbs.Length; i++)
        {
            bool isArm = i < 2;
            if ((isArm && !matchHands) || (!isArm && !matchFeet))
                continue;
            ref Limb limb = ref limbs[i];
            if (limb.upper == null || limb.lower == null || limb.end == null || limb.sourceEnd == null)
                continue;
            SolveTwoBone(limb.upper, limb.lower, limb.end, limb.sourceEnd.position, transform.rotation * limb.bendFallback);
            limb.end.rotation = limb.sourceEnd.rotation * limb.endOffset;
        }
    }

    // IK analítico de dois ossos: dobra o joelho/cotovelo no plano em que ele já está (a pose
    // copiada define a direção da dobra) até a ponta alcançar o alvo, depois aponta o membro.
    private static void SolveTwoBone(Transform a, Transform b, Transform c, Vector3 targetPosition, Vector3 fallbackHint)
    {
        Vector3 aPos = a.position, bPos = b.position, cPos = c.position;
        float lab = Vector3.Distance(aPos, bPos);
        float lcb = Vector3.Distance(cPos, bPos);
        if (lab < 1e-4f || lcb < 1e-4f)
            return;
        float lat = Mathf.Clamp(Vector3.Distance(targetPosition, aPos), 1e-3f, lab + lcb - 1e-3f);

        Vector3 ac = cPos - aPos;
        Vector3 axis = Vector3.Cross(ac, bPos - aPos);
        if (axis.sqrMagnitude < 1e-8f)
            axis = Vector3.Cross(ac, fallbackHint); // membro esticado: usa a direção padrão da dobra
        if (axis.sqrMagnitude < 1e-8f)
            return;
        axis.Normalize();

        float acAb0 = Angle(ac, bPos - aPos);
        float baBc0 = Angle(aPos - bPos, cPos - bPos);
        float acAb1 = SafeAcos((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat));
        float baBc1 = SafeAcos((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb));

        // Dobra (no plano atual) e depois aponta a raiz do membro para o alvo
        b.rotation = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg, axis) * b.rotation;
        a.rotation = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg, axis) * a.rotation;
        a.rotation = Quaternion.FromToRotation(c.position - a.position, targetPosition - a.position) * a.rotation;
    }

    private static float Angle(Vector3 from, Vector3 to)
    {
        return SafeAcos(Vector3.Dot(from.normalized, to.normalized));
    }

    private static float SafeAcos(float x)
    {
        return Mathf.Acos(Mathf.Clamp(x, -1f, 1f));
    }
}
