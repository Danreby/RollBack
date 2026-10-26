using System;
using System.Collections.Generic;
using MotionMatching;
using UnityEngine;

// Animação da locomoção do player por Learned Motion Matching (Holden et al., SIGGRAPH 2020).
//
// Em vez de um blend tree com "parado / correr" e crossfades, a cada quadro o personagem procura,
// num banco de movimento (ciclos de parado/andar/correr em várias velocidades, desarmado e com a
// espada), o trecho que melhor continua a pose atual e cumpre a trajetória que o controle pede
// para o próximo segundo. No modo aprendido o banco é substituído por três redes neurais pequenas
// (Projector = busca, Stepper = avançar, Decompressor = gerar a pose), treinadas em
// Tools/MotionMatching. Toda troca de trecho é suavizada por inertialização, os pés travam no chão
// durante o contato e viram passos quando precisam mudar de lugar.
//
// Golpes, esquiva e pulo continuam no Animator: quando um deles começa, a pose passa para o
// Animator com a mesma inertialização, e volta para o Motion Matching quando ele termina.
// F2 alterna o modo (LMM -> Motion Matching com banco -> Animator antigo) para comparar.
[DefaultExecutionOrder(-70)] // depois da animação (LateUpdate), antes da detecção de acerto e dos ajustes de pose
[RequireComponent(typeof(ThirdPersonController))]
public class MotionMatchingLocomotion : MonoBehaviour
{
    public enum Mode { LearnedMotionMatching, MotionMatching, Animator }

    [Tooltip("Banco de movimento e redes treinadas (gerado por Tools/MotionMatching)")]
    public TextAsset database;
    public Mode mode = Mode.LearnedMotionMatching;
    [Tooltip("Alterna o modo em jogo (comparação). None desliga")]
    public KeyCode cycleModeKey = KeyCode.F2;

    [Header("Busca")]
    [Tooltip("Intervalo entre buscas (s); mudanças bruscas no input buscam na hora")]
    public float searchInterval = 0.1f;

    [Header("Transições")]
    [Tooltip("Meia-vida (s) da inertialização: quanto tempo a diferença entre a pose antiga e a nova leva para cair pela metade")]
    public float inertializationHalflife = 0.1f;

    [Header("Raiz")]
    [Tooltip("Distância máxima (m) entre o corpo animado e a cápsula de colisão")]
    public float rootClampDistance = 0.15f;
    [Tooltip("Meia-vida (s) do ajuste que puxa o corpo animado para a cápsula")]
    public float rootAdjustmentHalflife = 0.2f;
    [Tooltip("A reprodução acelera/desacelera um pouco para o passo acompanhar a velocidade real")]
    public bool playbackWarping = true;

    [Header("Pés")]
    [Tooltip("Trava os pés no chão durante o contato (IK) e transforma deslizes em passos")]
    public bool footLocking = true;
    [Tooltip("Distância (m) a partir da qual um pé travado dá um passo até onde a animação o quer")]
    public float footUnlockRadius = 0.25f;

    [Header("Depuração")]
    public bool showDebugInfo;

    private ThirdPersonController movement;
    private PlayerCombat combat;
    private AnimationEventSink animationEvents;
    private bool leftContact, rightContact, leftStepping, rightStepping;
    private MotionDatabase db;
    private MotionMatcher matcher;
    private Transform[] bones;
    private Quaternion[] restRotations;
    private bool[] driven;
    private MotionPose external;
    private SimulationState sim;
    private float[] trajectoryTimes;

    public MotionMatcher Matcher => matcher;
    public bool IsDriving => matcher != null && matcher.IsMatching && mode != Mode.Animator;

    private void Awake()
    {
        movement = GetComponent<ThirdPersonController>();
        combat   = GetComponent<PlayerCombat>();
        animationEvents = GetComponent<AnimationEventSink>();
        if (database == null)
        {
            Debug.LogWarning("MotionMatchingLocomotion: sem banco de movimento, usando o Animator.", this);
            enabled = false;
            return;
        }

        try
        {
            db = MotionDatabase.Load(database.bytes);
        }
        catch (Exception e)
        {
            Debug.LogError($"MotionMatchingLocomotion: banco de movimento inválido ({e.Message}).", this);
            enabled = false;
            return;
        }

        if (!MapBones())
        {
            enabled = false;
            return;
        }

        matcher = new MotionMatcher(db);
        external = new MotionPose(db.BoneCount);
        sim = new SimulationState();
        trajectoryTimes = new float[db.TrajectoryFrames.Length];
        for (int i = 0; i < trajectoryTimes.Length; i++)
            trajectoryTimes[i] = db.TrajectoryFrames[i] / MotionDatabase.FrameRate;
        if (mode == Mode.LearnedMotionMatching && !db.HasNetworks)
            mode = Mode.MotionMatching;
    }

    // Liga cada osso do banco ao Transform de mesmo nome no personagem
    private bool MapBones()
    {
        var byName = new Dictionary<string, Transform>();
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name))
                byName.Add(t.name, t);

        bones = new Transform[db.BoneCount];
        restRotations = new Quaternion[db.BoneCount];
        driven = new bool[db.BoneCount];
        for (int i = 1; i < db.BoneCount; i++)
        {
            if (!byName.TryGetValue(db.BoneNames[i], out bones[i]))
            {
                Debug.LogError($"MotionMatchingLocomotion: osso '{db.BoneNames[i]}' não encontrado no personagem.", this);
                return false;
            }
            restRotations[i] = bones[i].localRotation;
        }

        // Ossos que o Animator (humanoide) anima; os demais ficam na pose padrão do modelo
        Animator animator = GetComponent<Animator>();
        if (animator != null && animator.isHuman)
        {
            var human = new HashSet<Transform>();
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (b == HumanBodyBones.LastBone)
                    continue;
                Transform t = animator.GetBoneTransform(b);
                if (t != null)
                    human.Add(t);
            }
            for (int i = 1; i < db.BoneCount; i++)
                driven[i] = human.Contains(bones[i]);
        }
        else
        {
            for (int i = 1; i < db.BoneCount; i++)
                driven[i] = true;
        }
        return true;
    }

    private void Update()
    {
        if (cycleModeKey != KeyCode.None && Input.GetKeyDown(cycleModeKey))
        {
            mode = (Mode)(((int)mode + 1) % 3);
            if (mode == Mode.LearnedMotionMatching && !db.HasNetworks)
                mode = Mode.MotionMatching;
            CombatHUD.SetPrompt(this, null);
            modeMessageUntil = Time.unscaledTime + 2f;
        }
        if (Time.unscaledTime < modeMessageUntil)
            CombatHUD.SetPrompt(this, "Locomoção: " + ModeName);
        else if (modeMessageUntil > 0f)
        {
            CombatHUD.SetPrompt(this, null);
            modeMessageUntil = 0f;
        }
    }

    private float modeMessageUntil;

    public string ModeName
    {
        get
        {
            switch (mode)
            {
                case Mode.LearnedMotionMatching: return "Learned Motion Matching";
                case Mode.MotionMatching: return "Motion Matching (banco)";
                default: return "Animator (blend tree antigo)";
            }
        }
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
        {
            // jogo pausado: o Animator reescreve a pose dele, então reaplica a última pose exibida
            if (releaseTimer <= inertializationHalflife * 6f)
                WritePose();
            return;
        }

        ReadAnimatorPose();
        ReadSimulation();
        matcher.UseLearned = mode == Mode.LearnedMotionMatching;
        ApplySettings();

        bool matching = mode != Mode.Animator && movement.InFreeLocomotion;
        int stance = combat != null && combat.IsArmed ? 1 : 0;
        matcher.Update(dt, sim, stance, matching, external);
        if (!PoseIsValid(matcher.Output))
        {
            // proteção: um valor inválido (NaN) contaminaria a pose para sempre; recomeça do zero
            Debug.LogWarning("MotionMatchingLocomotion: pose inválida, reiniciando o Motion Matching.", this);
            matcher = new MotionMatcher(db);
            return;
        }
        UpdateFootsteps(matching);

        // Modo Animator: depois que a diferença decai, a pose do Animator fica como está
        // (o núcleo continua rodando para a volta ao Motion Matching ser contínua)
        releaseTimer = mode == Mode.Animator ? releaseTimer + dt : 0f;
        if (releaseTimer > inertializationHalflife * 6f)
            return;
        WritePose();
    }

    private float releaseTimer;

    private static bool PoseIsValid(MotionPose pose)
    {
        Vec3 h = pose.HipsPosition;
        if (float.IsNaN(h.x + h.y + h.z) || float.IsInfinity(h.x + h.y + h.z))
            return false;
        foreach (Quat q in pose.Rotations)
            if (float.IsNaN(q.x + q.y + q.z + q.w))
                return false;
        return true;
    }

    // Som de passo quando um pé toca o chão (início do contato, ou fim de um passo procedural)
    private void UpdateFootsteps(bool matching)
    {
        if (animationEvents == null)
            return;
        animationEvents.ExternalFootsteps = matching;
        bool moving = movement.PlanarVelocity.sqrMagnitude > 0.3f * 0.3f;
        if (matching && ((moving && matcher.LeftFootContact && !leftContact) || (leftStepping && !matcher.LeftFootStepping)
                         || (moving && matcher.RightFootContact && !rightContact) || (rightStepping && !matcher.RightFootStepping)))
            animationEvents.ExternalStep();
        leftContact = matcher.LeftFootContact;
        rightContact = matcher.RightFootContact;
        leftStepping = matcher.LeftFootStepping;
        rightStepping = matcher.RightFootStepping;
    }

    private void ApplySettings()
    {
        matcher.SearchInterval = searchInterval;
        matcher.InertializationHalflife = inertializationHalflife;
        matcher.RootClampDistance = rootClampDistance / Scale;
        matcher.RootAdjustmentHalflife = rootAdjustmentHalflife;
        matcher.PlaybackWarping = playbackWarping;
        matcher.FootLocking = footLocking;
        matcher.FootUnlockRadius = footUnlockRadius / Scale;
    }

    private float Scale => Mathf.Max(1e-4f, transform.lossyScale.y);

    // Pose que o Animator produziu neste frame, no espaço do personagem (sem escala)
    private void ReadAnimatorPose()
    {
        Quaternion inverse = Quaternion.Inverse(transform.rotation);
        Transform hips = bones[db.Hips];
        external.HipsPosition = ToVec(transform.InverseTransformPoint(hips.position));
        external.Rotations[db.Hips] = ToQuat(inverse * hips.rotation);
        for (int i = 1; i < db.BoneCount; i++)
        {
            if (i == db.Hips)
                continue;
            external.Rotations[i] = ToQuat(driven[i] ? bones[i].localRotation : restRotations[i]);
        }
    }

    private void ReadSimulation()
    {
        float scale = Scale;
        sim.Position = ToVec(transform.position) / scale;
        sim.Rotation = ToQuat(transform.rotation);
        sim.Velocity = ToVec(movement.PlanarVelocity) / scale;
        sim.DesiredVelocity = ToVec(movement.DesiredVelocity) / scale;
        sim.DesiredRotation = ToQuat(movement.DesiredRotation);
        movement.PredictTrajectory(trajectoryTimes, sim.TrajectoryPositions, sim.TrajectoryRotations, scale);
    }

    private void WritePose()
    {
        MotionPose pose = matcher.Output;
        Transform hips = bones[db.Hips];
        hips.SetPositionAndRotation(transform.TransformPoint(ToVector(pose.HipsPosition)),
                                    transform.rotation * ToQuaternion(pose.Rotations[db.Hips]));
        for (int i = 1; i < db.BoneCount; i++)
            if (i != db.Hips)
                bones[i].localRotation = ToQuaternion(pose.Rotations[i]);
    }

    private void OnGUI()
    {
        if (!showDebugInfo || matcher == null)
            return;
        string range = matcher.IsMatching && matcher.CurrentSource == MotionMatcher.Source.Database
            ? $"trecho {matcher.CurrentRange} ({db.RangeSpeed[matcher.CurrentRange]:F1} m/s)" : "";
        GUI.Label(new Rect(20, 140, 700, 120),
                  $"{ModeName}  |  fonte: {matcher.CurrentSource} {range}\n" +
                  $"custo {matcher.LastSearchCost:F2}  transições {matcher.TransitionCount}  ritmo {matcher.PlaybackRate:F2}\n" +
                  $"raiz {matcher.RootOffset.Length * 100f:F1} cm  pés travados: {(matcher.LeftFootLocked ? "E" : "-")}{(matcher.RightFootLocked ? "D" : "-")}");
    }

    // Selecione o player na Scene: trajetória prevista pela simulação (amarelo, com a direção de
    // cada ponto) e o deslocamento entre o corpo animado e a cápsula (ciano).
    private void OnDrawGizmosSelected()
    {
        if (sim == null || matcher == null || !Application.isPlaying)
            return;
        float scale = Scale;
        Vector3 previous = transform.position;
        Gizmos.color = Color.yellow;
        for (int i = 0; i < sim.TrajectoryPositions.Length; i++)
        {
            Vector3 p = ToVector(sim.TrajectoryPositions[i]) * scale;
            Gizmos.DrawLine(previous, p);
            Gizmos.DrawWireSphere(p, 0.08f * scale);
            Gizmos.DrawLine(p, p + ToQuaternion(sim.TrajectoryRotations[i]) * Vector3.forward * 0.4f * scale);
            previous = p;
        }
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + ToVector(matcher.RootOffset) * scale);
    }

    private void OnDisable()
    {
        CombatHUD.SetPrompt(this, null);
        if (animationEvents != null)
            animationEvents.ExternalFootsteps = false;
    }

    private static Vec3 ToVec(Vector3 v) { return new Vec3(v.x, v.y, v.z); }
    private static Vector3 ToVector(Vec3 v) { return new Vector3(v.x, v.y, v.z); }
    private static Quat ToQuat(Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }
    private static Quaternion ToQuaternion(Quat q) { return new Quaternion(q.x, q.y, q.z, q.w); }
}
