using System;

namespace MotionMatching
{
    // Pose no espaço da simulação: rotações locais de cada osso (o quadril, índice 1, relativo à
    // simulação) e posição do quadril. Guarda também as velocidades usadas pela inertialização.
    public sealed class MotionPose
    {
        public readonly Quat[] Rotations;
        public readonly Vec3[] AngularVelocities;
        public Vec3 HipsPosition;
        public Vec3 HipsVelocity;

        public MotionPose(int bones)
        {
            Rotations = new Quat[bones];
            AngularVelocities = new Vec3[bones];
            for (int i = 0; i < bones; i++)
                Rotations[i] = Quat.Identity;
        }

        public void CopyFrom(MotionPose other)
        {
            Array.Copy(other.Rotations, Rotations, Rotations.Length);
            Array.Copy(other.AngularVelocities, AngularVelocities, AngularVelocities.Length);
            HipsPosition = other.HipsPosition;
            HipsVelocity = other.HipsVelocity;
        }

        // Velocidades por diferença finita em relação a uma pose anterior
        public void DifferenceFrom(MotionPose previous, float dt)
        {
            for (int i = 1; i < Rotations.Length; i++)
                AngularVelocities[i] = Quat.ToScaledAngleAxis((Rotations[i] * previous.Rotations[i].Inverse).Abs) / dt;
            HipsVelocity = (HipsPosition - previous.HipsPosition) / dt;
        }
    }

    // Dados da simulação do personagem num frame (unidades do esqueleto, sem escala).
    public sealed class SimulationState
    {
        public Vec3 Position;
        public Quat Rotation = Quat.Identity;
        public Vec3 Velocity;
        public Vec3 DesiredVelocity;
        public Quat DesiredRotation = Quat.Identity;
        public readonly Vec3[] TrajectoryPositions = new Vec3[3];
        public readonly Quat[] TrajectoryRotations = new Quat[3];
    }

    // Motion Matching com a variante aprendida (Learned Motion Matching).
    //
    // A cada quadro: (1) monta a consulta com a pose atual e a trajetória prevista pela simulação;
    // (2) periodicamente, ou quando o input muda de repente, procura o trecho do banco que melhor
    // continua o movimento (modo banco: busca exaustiva; modo aprendido: o Projector faz a busca);
    // (3) avança a animação (modo banco: próximo frame; modo aprendido: Stepper) e gera a pose
    // (modo aprendido: Decompressor); (4) toda troca de trecho, e a troca entre Motion Matching e o
    // Animator (golpes, esquiva, pulo), é suavizada por inertialização: a diferença entre a pose
    // antiga e a nova decai como uma mola, sem misturar duas animações; (5) a raiz visual segue o
    // deslocamento da animação e é puxada de volta para a cápsula sem deslizar os pés; (6) pés em
    // contato com o chão ficam travados no lugar (IK de duas articulações) e, quando a animação
    // muda o pé de lugar sem levantá-lo (arrancadas, paradas), ele dá um passo em arco.
    public sealed class MotionMatcher
    {
        public enum Source { None, Database, Learned, External }

        public readonly MotionDatabase Db;

        // ---- Ajustes ----
        public bool UseLearned = true;
        public float SearchInterval = 0.1f;
        public float InertializationHalflife = 0.1f;
        public int IgnoreSurroundingFrames = 8;
        public float TransitionCostBias = 0.01f;
        // Abaixo disto (m/s), sem input, o personagem é considerado parado (entra no idle)
        public float MovingSpeedThreshold = 0.8f;
        public float VelocityChangeThreshold = 50f;
        public float RotationChangeThreshold = 50f;
        public float RootAdjustmentHalflife = 0.2f;
        public float RootAdjustmentMaxRatio = 0.5f;
        public float RootAdjustmentMinSpeed = 0.1f;
        public float RootClampDistance = 0.15f;
        // Ajuste contínuo da velocidade de reprodução para o passo acompanhar a simulação
        public bool PlaybackWarping = true;
        public float WarpMin = 0.6f;
        public float WarpMax = 1.3f;
        public float WarpHalflife = 0.08f;
        public bool FootLocking = true;
        public float FootUnlockRadius = 0.25f;
        public float FootLockHalflife = 0.1f;
        public float FootMaxLockRadius = 0.5f;
        public float FootSettleSpeed = 0.6f;
        public float FootStepSpeed = 2.2f;
        public float FootStepMinTime = 0.16f;
        public float FootStepMaxTime = 0.32f;
        public float FootStepHeight = 0.3f;
        public float FootStepMaxHeight = 0.14f;
        public float FootGroundClearance = 0.005f;

        // ---- Saída ----
        public readonly MotionPose Output;            // pose final (com IK dos pés)
        public bool IsMatching { get; private set; }
        public Source CurrentSource { get; private set; }
        public int CurrentFrame => frame;
        public int CurrentRange => Db.FrameRange[frame];
        public float LastSearchCost { get; private set; }
        public int TransitionCount { get; private set; }
        public Vec3 RootOffset => rootOffset;
        public float PlaybackRate => playbackRate;
        public bool LeftFootLocked => contacts[0].locked;
        public bool RightFootLocked => contacts[1].locked;
        public bool LeftFootStepping => contacts[0].stepping;
        public bool RightFootStepping => contacts[1].stepping;
        public MotionPose AnimatedPose => inertialized;   // pose antes do IK dos pés (depuração)
        public bool LeftFootContact => sourceContacts[0];
        public bool RightFootContact => sourceContacts[1];
        public readonly float[] Query;

        private readonly int nb;
        private readonly int nf;
        private readonly MotionPose inertialized;      // saída da inertialização (antes do IK)
        private readonly MotionPose previousOutput;
        private readonly MotionPose source;            // pose da fonte atual (banco/rede/Animator) neste frame
        private readonly MotionPose candidate;
        private readonly MotionPose previousExternal;
        private bool hasPreviousExternal;
        private bool hasPreviousOutput;

        // Inertialização
        private readonly Quat[] offsetRotations;
        private readonly Vec3[] offsetAngular;
        private Vec3 offsetHips, offsetHipsVelocity;

        // Estado do modo banco
        private int frame;
        private float frameFraction;
        // Estado do modo aprendido
        private readonly float[] X, Z, netIn, stepOut, decOut, projOut;
        private readonly float[] lookX, lookZ;

        private Source matchingSource = Source.None;
        private int stance;
        private float searchTimer;
        private Vec3 rootOffset;
        private Vec3 lastSimPosition;
        private bool hasLastSim;
        private Vec3 lastDesiredVelocity, lastDesiredVelocityChange;
        private Quat lastDesiredRotation = Quat.Identity;
        private Vec3 lastDesiredRotationChange;
        private Vec3 rootVelocity;               // da fonte, no espaço da raiz
        private float playbackRate = 1f;
        private bool relockFeet;
        private readonly bool[] sourceContacts = new bool[2];

        // Features da pose exibida (para voltar ao Motion Matching depois do Animator)
        private readonly Vec3[] fkPos, fkPrevWorld;
        private readonly Quat[] fkRot;
        private readonly float[] outputFeatures;
        private bool hasPrevFeatures;

        private struct Contact
        {
            public bool state, locked, hasTarget, wantLock, stepping;
            public float stepTime, stepDuration;
            public Vec3 stepFrom;
            public Vec3 point, target, offset, offsetVelocity;
        }
        private readonly Contact[] contacts = new Contact[2];
        private readonly Vec3[] kneeAxes = new Vec3[2];   // eixo de dobra do joelho no espaço da coxa

        public MotionMatcher(MotionDatabase db)
        {
            Db = db;
            nb = db.BoneCount;
            nf = db.FeatureCount;
            Output = new MotionPose(nb);
            inertialized = new MotionPose(nb);
            previousOutput = new MotionPose(nb);
            source = new MotionPose(nb);
            candidate = new MotionPose(nb);
            previousExternal = new MotionPose(nb);
            offsetRotations = new Quat[nb];
            offsetAngular = new Vec3[nb];
            for (int i = 0; i < nb; i++) offsetRotations[i] = Quat.Identity;
            Query = new float[nf];
            X = new float[nf];
            Z = new float[Math.Max(1, db.LatentCount)];
            lookX = new float[nf];
            lookZ = new float[Z.Length];
            netIn = new float[nf + Z.Length];
            stepOut = new float[nf + Z.Length];
            decOut = new float[db.HasNetworks ? db.Decompressor.OutputSize : 1];
            projOut = new float[nf + Z.Length];
            fkPos = new Vec3[nb];
            fkRot = new Quat[nb];
            fkPrevWorld = new Vec3[3];
            outputFeatures = new float[nf];
            kneeAxes[0] = KneeAxis(db.LeftUpperLeg, db.LeftLowerLeg, db.LeftFoot);
            kneeAxes[1] = KneeAxis(db.RightUpperLeg, db.RightLowerLeg, db.RightFoot);
            // pose inicial: primeiro frame do banco (parado, desarmado)
            SampleDatabase(0, 0f, source);
            inertialized.CopyFrom(source);
            Output.CopyFrom(source);
            previousOutput.CopyFrom(source);
            Array.Copy(db.Features, 0, outputFeatures, 0, nf);
        }

        // matching = true: a locomoção é do Motion Matching; false: a pose vem de 'external' (Animator).
        public void Update(float dt, SimulationState sim, int desiredStance, bool matching, MotionPose external)
        {
            if (dt <= 1e-6f)
                return;
            if (!hasLastSim)
            {
                lastSimPosition = sim.Position;
                hasLastSim = true;
            }

            if (external != null)
            {
                if (hasPreviousExternal)
                    external.DifferenceFrom(previousExternal, dt);
                previousExternal.CopyFrom(external);
                hasPreviousExternal = true;
            }

            if (matching || external == null)
                UpdateMatching(dt, sim, desiredStance);
            else
                UpdateExternal(dt, external);

            UpdateFootLocking(dt, sim);
            UpdateOutputFeatures(dt, sim);
            lastSimPosition = sim.Position;
        }

        // ------------------------------------------------------------ Motion Matching

        private void UpdateMatching(float dt, SimulationState sim, int desiredStance)
        {
            bool entering = !IsMatching;
            Source wanted = UseLearned && Db.HasNetworks ? Source.Learned : Source.Database;
            if (wanted != matchingSource)
                entering = true; // trocou de modo: refaz a busca a partir da pose exibida
            if (entering)
            {
                rootOffset = Vec3.Zero;
                lastDesiredVelocity = sim.DesiredVelocity;
                lastDesiredRotation = sim.DesiredRotation;
                lastDesiredVelocityChange = Vec3.Zero;
                lastDesiredRotationChange = Vec3.Zero;
            }

            // Input mudou de repente (ex.: direcional invertido): busca já, sem esperar o intervalo
            Vec3 velocityChange = (sim.DesiredVelocity - lastDesiredVelocity) / dt;
            Vec3 rotationChange = Quat.ToScaledAngleAxis((sim.DesiredRotation * lastDesiredRotation.Inverse).Abs) / dt;
            bool abrupt = (velocityChange.Length > VelocityChangeThreshold && lastDesiredVelocityChange.Length <= VelocityChangeThreshold)
                          || (rotationChange.Length > RotationChangeThreshold && lastDesiredRotationChange.Length <= RotationChangeThreshold);
            lastDesiredVelocityChange = velocityChange;
            lastDesiredRotationChange = rotationChange;
            lastDesiredVelocity = sim.DesiredVelocity;
            lastDesiredRotation = sim.DesiredRotation;

            searchTimer -= dt;
            bool search = entering || abrupt || searchTimer <= 0f || desiredStance != stance;
            stance = desiredStance;

            if (search)
            {
                BuildQuery(sim, entering);
                bool transition = wanted == Source.Learned ? SearchLearned(entering) : SearchDatabase(entering);
                if (transition)
                {
                    // pose do novo trecho no instante atual (com velocidades) -> inertialização
                    CurrentSourcePose(wanted, sim, candidate, true);
                    BeginTransition(candidate);
                    TransitionCount++;
                    relockFeet = true;
                }
                searchTimer = SearchInterval;
            }
            matchingSource = wanted;
            IsMatching = true;
            CurrentSource = wanted;

            // Avança a animação. Com warping, a reprodução acelera/desacelera um pouco para o
            // deslocamento da animação acompanhar a velocidade real (arrancadas e paradas).
            UpdatePlaybackRate(dt, sim);
            float step = dt * playbackRate;
            if (wanted == Source.Learned)
                StepLearned(step);
            else
                StepDatabase(step);

            UpdateRootOffset(dt, sim);
            CurrentSourcePose(wanted, sim, source, false);
            UpdateInertializer(source, dt);
        }

        private void UpdateExternal(float dt, MotionPose external)
        {
            if (IsMatching || !hasPreviousOutput)
            {
                // Animator assumiu (golpe, esquiva, pulo): a diferença decai a partir da pose exibida
                if (hasPreviousOutput)
                    BeginTransition(external);
                IsMatching = false;
                matchingSource = Source.None;
            }
            CurrentSource = Source.External;
            sourceContacts[0] = sourceContacts[1] = false;
            UpdateInertializer(external, dt);
        }

        // ------------------------------------------------------------ Consulta

        private void BuildQuery(SimulationState sim, bool entering)
        {
            // Parte da pose: features do frame atual, ou da pose exibida quando volta do Animator
            int poseFeatures = Db.TrajectoryPositionFeature;
            if (entering)
                Array.Copy(outputFeatures, Query, poseFeatures);
            else if (matchingSource == Source.Learned)
                Array.Copy(X, Query, poseFeatures);
            else
                Array.Copy(Db.Features, NearestFrame() * nf, Query, 0, poseFeatures);

            // Trajetória desejada, relativa à raiz visual do personagem
            Vec3 origin = sim.Position + rootOffset;
            Quat inv = sim.Rotation.Inverse;
            int tp = Db.TrajectoryPositionFeature, td = Db.TrajectoryDirectionFeature;
            for (int k = 0; k < 3; k++)
            {
                Vec3 p = inv * (sim.TrajectoryPositions[k] - origin);
                Vec3 d = inv * (sim.TrajectoryRotations[k] * Vec3.Forward);
                Query[tp + k * 2 + 0] = Normalize(tp + k * 2 + 0, p.x);
                Query[tp + k * 2 + 1] = Normalize(tp + k * 2 + 1, p.z);
                Query[td + k * 2 + 0] = Normalize(td + k * 2 + 0, d.x);
                Query[td + k * 2 + 1] = Normalize(td + k * 2 + 1, d.z);
            }
            Db.WriteStance(Query, stance);

            Vec3 velocity = inv * sim.Velocity;
            int rv = Db.RootVelocityFeature;
            Query[rv] = Normalize(rv, velocity.x);
            Query[rv + 1] = Normalize(rv + 1, velocity.z);
            bool moving = sim.DesiredVelocity.Length > 0.1f || sim.Velocity.Planar.Length > MovingSpeedThreshold;
            Query[Db.MovingFeature] = Normalize(Db.MovingFeature, moving ? 1f : 0f);
        }

        private float Normalize(int i, float raw)
        {
            return (raw - Db.FeatureOffset[i]) / Db.FeatureScale[i];
        }

        private float Cost(float[] a, int aOffset, float[] b, float limit)
        {
            float cost = 0f;
            for (int i = 0; i < nf; i++)
            {
                float d = a[aOffset + i] - b[i];
                cost += d * d;
                if (cost > limit) break;
            }
            return cost;
        }

        // Busca exaustiva no banco (só nos trechos da postura pedida)
        private bool SearchDatabase(bool entering)
        {
            int current = NearestFrame();
            int currentRange = Db.FrameRange[current];
            bool currentValid = !entering && matchingSource == Source.Database && Db.RangeStance[currentRange] == stance;
            float bestCost = currentValid ? Cost(Db.Features, current * nf, Query, float.MaxValue) - TransitionCostBias : float.MaxValue;
            int best = -1;
            for (int r = 0; r < Db.RangeCount; r++)
            {
                if (Db.RangeStance[r] != stance)
                    continue;
                int start = Db.RangeStart[r], stop = Db.RangeStop[r], length = stop - start;
                for (int f = start; f < stop; f++)
                {
                    if (currentValid && r == currentRange)
                    {
                        int dist = Math.Abs(f - current);
                        dist = Math.Min(dist, length - dist);
                        if (dist <= IgnoreSurroundingFrames)
                            continue;
                    }
                    float c = Cost(Db.Features, f * nf, Query, bestCost);
                    if (c < bestCost)
                    {
                        bestCost = c;
                        best = f;
                    }
                }
            }
            LastSearchCost = bestCost;
            if (best < 0)
                return false;
            frame = best;
            frameFraction = 0f;
            return true;
        }

        // Learned Motion Matching: o Projector devolve as features/latentes do frame mais parecido
        private bool SearchLearned(bool entering)
        {
            Db.Projector.Evaluate(Query, projOut);
            float projected = 0f, current = 0f;
            for (int i = 0; i < nf; i++)
            {
                float d = Query[i] - projOut[i];
                projected += d * d;
                float e = Query[i] - X[i];
                current += e * e;
            }
            LastSearchCost = projected;
            if (!entering && matchingSource == Source.Learned && projected + TransitionCostBias >= current)
                return false;
            Array.Copy(projOut, 0, X, 0, nf);
            Array.Copy(projOut, nf, Z, 0, Z.Length);
            return true;
        }

        // ------------------------------------------------------------ Reprodução

        private int NearestFrame()
        {
            return frameFraction < 0.5f ? frame : Db.NextFrame[frame];
        }

        private void StepDatabase(float dt)
        {
            frameFraction += dt * MotionDatabase.FrameRate;
            while (frameFraction >= 1f)
            {
                frame = Db.NextFrame[frame];
                frameFraction -= 1f;
            }
        }

        private void StepLearned(float dt)
        {
            Step(X, Z, X, Z, dt);
        }

        private void Step(float[] x, float[] z, float[] xOut, float[] zOut, float dt)
        {
            Array.Copy(x, 0, netIn, 0, nf);
            Array.Copy(z, 0, netIn, nf, Z.Length);
            Db.Stepper.Evaluate(netIn, stepOut);
            for (int i = 0; i < nf; i++) xOut[i] = x[i] + dt * stepOut[i];
            for (int i = 0; i < Z.Length; i++) zOut[i] = z[i] + dt * stepOut[nf + i];
        }

        // Pose do modo banco entre dois frames (com as velocidades gravadas)
        private void SampleDatabase(int f, float t, MotionPose pose)
        {
            int g = Db.NextFrame[f];
            int a = f * nb, b = g * nb;
            for (int i = 1; i < nb; i++)
            {
                pose.Rotations[i] = Quat.Slerp(Db.Rotations[a + i], Db.Rotations[b + i], t);
                pose.AngularVelocities[i] = Vec3.Lerp(Db.AngularVelocities[a + i], Db.AngularVelocities[b + i], t);
            }
            pose.HipsPosition = Vec3.Lerp(Db.HipsPositions[f], Db.HipsPositions[g], t);
            pose.HipsVelocity = Vec3.Lerp(Db.HipsVelocities[f], Db.HipsVelocities[g], t);
            rootVelocity = Vec3.Lerp(Db.RootVelocities[f], Db.RootVelocities[g], t);
            int n = t < 0.5f ? f : g;
            sourceContacts[0] = Db.Contacts[n * 2];
            sourceContacts[1] = Db.Contacts[n * 2 + 1];
        }

        // Pose gerada pela rede a partir de (x, z)
        private void Decompress(float[] x, float[] z, MotionPose pose)
        {
            Array.Copy(x, 0, netIn, 0, nf);
            Array.Copy(z, 0, netIn, nf, Z.Length);
            Db.Decompressor.Evaluate(netIn, decOut);
            pose.HipsPosition = new Vec3(decOut[0], decOut[1], decOut[2]);
            int o = 3;
            for (int i = 1; i < nb; i++, o += 6)
                pose.Rotations[i] = DecodeRotation(decOut, o);
            rootVelocity = new Vec3(decOut[o], decOut[o + 1], decOut[o + 2]);
            sourceContacts[0] = decOut[o + 6] > 0.5f;
            sourceContacts[1] = decOut[o + 7] > 0.5f;
        }

        // Duas colunas da matriz de rotação, gravadas por linha: (c0x c1x c0y c1y c0z c1z)
        public static Quat DecodeRotation(float[] data, int o)
        {
            return Quat.FromTwoColumns(data[o], data[o + 2], data[o + 4], data[o + 1], data[o + 3], data[o + 5]);
        }

        // Pose da fonte do Motion Matching no espaço da simulação (raiz visual = simulação + offset)
        private void CurrentSourcePose(Source kind, SimulationState sim, MotionPose pose, bool withVelocities)
        {
            if (kind == Source.Learned)
            {
                Decompress(X, Z, pose);
                if (withVelocities)
                {
                    // velocidade: um passo à frente com o Stepper
                    Step(X, Z, lookX, lookZ, 1f / MotionDatabase.FrameRate);
                    Vec3 savedRoot = rootVelocity;
                    bool c0 = sourceContacts[0], c1 = sourceContacts[1];
                    Decompress(lookX, lookZ, scratchLook);
                    float inv = MotionDatabase.FrameRate;
                    for (int i = 1; i < nb; i++)
                        pose.AngularVelocities[i] = Quat.ToScaledAngleAxis((scratchLook.Rotations[i] * pose.Rotations[i].Inverse).Abs) * inv;
                    pose.HipsVelocity = (scratchLook.HipsPosition - pose.HipsPosition) * inv;
                    rootVelocity = savedRoot;
                    sourceContacts[0] = c0; sourceContacts[1] = c1;
                }
            }
            else
            {
                SampleDatabase(frame, frameFraction, pose);
            }
            // a raiz visual pode estar um pouco fora da cápsula (ajuste sem deslizar os pés)
            pose.HipsPosition += sim.Rotation.Inverse * rootOffset;
        }

        private MotionPose scratchLookPose;
        private MotionPose scratchLook => scratchLookPose ?? (scratchLookPose = new MotionPose(nb));

        private void UpdatePlaybackRate(float dt, SimulationState sim)
        {
            float target = 1f;
            float animationSpeed = rootVelocity.Planar.Length;
            if (PlaybackWarping && animationSpeed > 0.5f)
            {
                float simulationSpeed = sim.Velocity.Planar.Length;
                target = Clamp(simulationSpeed / animationSpeed, WarpMin, WarpMax);
            }
            playbackRate += (target - playbackRate) * (1f - Springs.NegExp(Springs.Ln2 * dt / WarpHalflife));
        }

        // Raiz visual: anda com a animação e é puxada para a simulação no máximo a uma fração
        // da própria velocidade (assim o ajuste não aparece como pé deslizando); nunca se afasta
        // mais que RootClampDistance.
        private void UpdateRootOffset(float dt, SimulationState sim)
        {
            Vec3 animationVelocity = (sim.Rotation * rootVelocity).Planar * playbackRate;
            Vec3 simulationDelta = (sim.Position - lastSimPosition).Planar;
            rootOffset += animationVelocity * dt - simulationDelta;

            Vec3 adjustment = Springs.DampAdjustmentExact(-rootOffset, RootAdjustmentHalflife, dt);
            float maxLength = (RootAdjustmentMaxRatio * animationVelocity.Length + RootAdjustmentMinSpeed) * dt;
            if (adjustment.Length > maxLength)
                adjustment = adjustment.Normalized() * maxLength;
            rootOffset += adjustment;
            if (rootOffset.Length > RootClampDistance)
                rootOffset = rootOffset.Normalized() * RootClampDistance;
            rootOffset.y = 0f;
        }

        // ------------------------------------------------------------ Inertialização

        private void BeginTransition(MotionPose destination)
        {
            // offset = (pose exibida) - (nova pose); decai até zero em UpdateInertializer
            for (int i = 1; i < nb; i++)
            {
                offsetRotations[i] = (inertialized.Rotations[i] * destination.Rotations[i].Inverse).Abs;
                offsetAngular[i] = inertialized.AngularVelocities[i] - destination.AngularVelocities[i];
            }
            offsetHips = inertialized.HipsPosition - destination.HipsPosition;
            offsetHipsVelocity = inertialized.HipsVelocity - destination.HipsVelocity;
        }

        private void UpdateInertializer(MotionPose input, float dt)
        {
            previousOutput.CopyFrom(inertialized);
            for (int i = 1; i < nb; i++)
            {
                Springs.DecaySpringDamperExact(ref offsetRotations[i], ref offsetAngular[i], InertializationHalflife, dt);
                inertialized.Rotations[i] = (offsetRotations[i] * input.Rotations[i]).Normalized;
            }
            Springs.DecaySpringDamperExact(ref offsetHips, ref offsetHipsVelocity, InertializationHalflife, dt);
            inertialized.HipsPosition = input.HipsPosition + offsetHips;
            if (hasPreviousOutput)
                inertialized.DifferenceFrom(previousOutput, dt);
            hasPreviousOutput = true;
            Output.CopyFrom(inertialized);
        }

        // ------------------------------------------------------------ Pés travados no chão

        private void ForwardKinematics(MotionPose pose, SimulationState sim)
        {
            fkRot[0] = sim.Rotation;
            fkPos[0] = sim.Position;
            for (int i = 1; i < nb; i++)
            {
                int p = Db.BoneParents[i];
                Vec3 local = i == Db.Hips ? pose.HipsPosition : Db.BindLocalPositions[i];
                fkPos[i] = fkPos[p] + fkRot[p] * local;
                fkRot[i] = fkRot[p] * pose.Rotations[i];
            }
        }

        // Cada pé: Solto (segue a animação; diferenças decaem como mola), Travado (parado no ponto
        // onde pousou) ou Passo (vai do ponto travado até onde a animação o quer, num arco, quando
        // a animação mudou de lugar sem levantar o pé: arrancadas, paradas, giros).
        private void UpdateFootLocking(float dt, SimulationState sim)
        {
            ForwardKinematics(Output, sim);
            float ground = sim.Position.y + FootGroundClearance;
            for (int f = 0; f < 2; f++)
            {
                int toe = f == 0 ? Db.LeftToes : Db.RightToes;
                bool inputContact = FootLocking && IsMatching && sourceContacts[f];
                Vec3 inputPosition = fkPos[toe];
                ref Contact c = ref contacts[f];
                bool otherStepping = contacts[1 - f].stepping;

                Vec3 inputVelocity = c.hasTarget ? (inputPosition - c.target) / dt : Vec3.Zero;
                c.target = inputPosition;
                c.hasTarget = true;

                Springs.DecaySpringDamperExact(ref c.offset, ref c.offsetVelocity, FootLockHalflife, dt);

                if (inputContact && (!c.state || relockFeet))
                    c.wantLock = true;      // começou um contato, ou trocou de trecho com o pé no chão
                if (!inputContact)
                    c.wantLock = false;

                Vec3 shown;
                if (c.locked)
                {
                    shown = c.point;
                    float distance = (c.point - inputPosition).Length;
                    bool settled = inputVelocity.Planar.Length < FootSettleSpeed;
                    if (!inputContact)
                        Release(ref c, shown, inputPosition, inputVelocity);       // a animação levanta o pé
                    else if (distance > FootMaxLockRadius || (distance > FootUnlockRadius && settled && !otherStepping))
                    {
                        c.locked = false;
                        c.stepping = true;
                        c.stepFrom = c.point;
                        c.stepTime = 0f;
                        c.stepDuration = Clamp(distance / FootStepSpeed, FootStepMinTime, FootStepMaxTime);
                    }
                }
                else if (c.stepping)
                {
                    c.stepTime += dt;
                    float p = Clamp(c.stepTime / c.stepDuration, 0f, 1f);
                    float eased = p * p * (3f - 2f * p);
                    float distance = (inputPosition - c.stepFrom).Planar.Length;
                    shown = Vec3.Lerp(c.stepFrom, inputPosition, eased);
                    shown.y += Math.Min(FootStepHeight * distance, FootStepMaxHeight) * 4f * p * (1f - p);
                    if (!inputContact)
                    {
                        c.stepping = false;
                        Release(ref c, shown, inputPosition, inputVelocity);
                    }
                    else if (p >= 1f)
                    {
                        c.stepping = false;
                        Lock(ref c, inputPosition, inputPosition.y, ground);
                    }
                }
                else
                {
                    shown = inputPosition + c.offset;
                    if (c.wantLock)
                    {
                        Lock(ref c, shown, inputPosition.y, ground);
                        shown = c.point + c.offset;
                    }
                }
                if (c.locked)
                    shown = c.point + c.offset;
                c.state = inputContact;

                // nada atravessa o chão (inertialização entre poses com alturas de quadril diferentes)
                if (IsMatching && shown.y < ground)
                    shown.y = ground;
                Vec3 delta = shown - inputPosition;
                if (delta.SqrLength > 1e-8f)
                    SolveLeg(f, delta, sim);
            }
            relockFeet = false;
        }

        private static void Lock(ref Contact c, Vec3 at, float animatedHeight, float ground)
        {
            c.locked = true;
            c.wantLock = false;
            // posição horizontal onde o pé está; altura da animação, que diz que ele já está no chão
            c.point = new Vec3(at.x, Math.Max(animatedHeight, ground), at.z);
            c.offset = at - c.point;
            // o pé para ao tocar o chão (não continua escorregando nem afundando)
            c.offsetVelocity = Vec3.Zero;
        }

        private static void Release(ref Contact c, Vec3 shown, Vec3 input, Vec3 inputVelocity)
        {
            c.locked = false;
            c.offset = shown - input;
            c.offsetVelocity = -inputVelocity;
        }

        // IK de duas articulações (coxa, canela) levando o pé a 'delta' de onde a animação o colocou;
        // o pé mantém a orientação da animação.
        private void SolveLeg(int side, Vec3 delta, SimulationState sim)
        {
            int hip = side == 0 ? Db.LeftUpperLeg : Db.RightUpperLeg;
            int knee = side == 0 ? Db.LeftLowerLeg : Db.RightLowerLeg;
            int foot = side == 0 ? Db.LeftFoot : Db.RightFoot;
            Vec3 a = fkPos[hip], b = fkPos[knee], c = fkPos[foot];
            Vec3 t = c + delta;
            Quat footWorld = fkRot[foot];

            float lab = (b - a).Length, lbc = (c - b).Length;
            if (lab < 1e-5f || lbc < 1e-5f)
                return;
            float lat = Clamp((t - a).Length, Math.Abs(lab - lbc) + 1e-4f, lab + lbc - 1e-4f);

            Vec3 ba = a - b, bc = c - b;
            // Eixo de dobra do joelho fixo na coxa (anatômico): com a perna quase esticada o plano
            // atual da perna é instável e o joelho poderia "virar" para o lado de um frame para o outro.
            Vec3 axis = fkRot[hip] * kneeAxes[side];
            float current = Angle(ba, bc);
            float desired = (float)Math.Acos(Clamp((lab * lab + lbc * lbc - lat * lat) / (2f * lab * lbc), -1f, 1f));
            Quat kneeTurn = Quat.FromAngleAxis(desired - current, axis);
            Quat kneeWorld = kneeTurn * fkRot[knee];
            Vec3 cNew = b + kneeTurn * (c - b);

            Quat hipTurn = FromTo(cNew - a, t - a);
            Quat hipWorld = hipTurn * fkRot[hip];
            kneeWorld = hipTurn * kneeWorld;

            int parent = Db.BoneParents[hip];
            Output.Rotations[hip] = (fkRot[parent].Inverse * hipWorld).Normalized;
            Output.Rotations[knee] = (hipWorld.Inverse * kneeWorld).Normalized;
            Output.Rotations[foot] = (kneeWorld.Inverse * footWorld).Normalized;
            fkRot[hip] = hipWorld;
            fkRot[knee] = kneeWorld;
            fkPos[knee] = a + hipTurn * (b - a);
            fkPos[foot] = t;
        }

        // Média, no espaço da coxa, do eixo em torno do qual o joelho dobra nos frames do banco
        private Vec3 KneeAxis(int hip, int knee, int foot)
        {
            Vec3 sum = Vec3.Zero;
            var wr = new Quat[nb];
            var wp = new Vec3[nb];
            wr[0] = Quat.Identity;
            for (int f = 0; f < Db.FrameCount; f += 4)
            {
                for (int i = 1; i < nb; i++)
                {
                    int parent = Db.BoneParents[i];
                    Vec3 local = i == Db.Hips ? Db.HipsPositions[f] : Db.BindLocalPositions[i];
                    wp[i] = wp[parent] + wr[parent] * local;
                    wr[i] = wr[parent] * Db.Rotations[f * nb + i];
                }
                Vec3 ba = wp[hip] - wp[knee], bc = wp[foot] - wp[knee];
                Vec3 n = Vec3.Cross(ba, bc);
                if (Angle(ba, bc) < 2.8f && n.Length > 1e-6f) // joelho dobrado (menos de ~160 graus)
                    sum += wr[hip].Inverse * n.Normalized();
            }
            return sum.Length > 1e-6f ? sum.Normalized() : new Vec3(-1f, 0f, 0f);
        }

        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private static float Angle(Vec3 u, Vec3 v)
        {
            float d = Vec3.Dot(u.Normalized(), v.Normalized());
            return (float)Math.Acos(Clamp(d, -1f, 1f));
        }

        private static Quat FromTo(Vec3 u, Vec3 v)
        {
            Vec3 axis = Vec3.Cross(u, v);
            float angle = Angle(u, v);
            if (axis.Length < 1e-8f || angle < 1e-6f)
                return Quat.Identity;
            return Quat.FromAngleAxis(angle, axis);
        }

        // ------------------------------------------------------------ Features da pose exibida

        private void UpdateOutputFeatures(float dt, SimulationState sim)
        {
            ForwardKinematics(inertialized, sim);
            Quat inv = sim.Rotation.Inverse;
            for (int k = 0; k < 3; k++)
            {
                Vec3 world = fkPos[k == 0 ? Db.LeftFoot : (k == 1 ? Db.RightFoot : Db.Hips)];
                Vec3 velocity = hasPrevFeatures ? inv * ((world - fkPrevWorld[k]) / dt) : Vec3.Zero;
                fkPrevWorld[k] = world;
                if (k < 2)
                {
                    Vec3 local = inv * (world - sim.Position);
                    int pf = k == 0 ? Db.LeftFootPositionFeature : Db.RightFootPositionFeature;
                    int vf = k == 0 ? Db.LeftFootVelocityFeature : Db.RightFootVelocityFeature;
                    SetFeature(pf, local);
                    SetFeature(vf, velocity);
                }
                else
                {
                    SetFeature(Db.HipVelocityFeature, velocity);
                }
            }
            hasPrevFeatures = true;
        }

        private void SetFeature(int start, Vec3 v)
        {
            outputFeatures[start] = Normalize(start, v.x);
            outputFeatures[start + 1] = Normalize(start + 1, v.y);
            outputFeatures[start + 2] = Normalize(start + 2, v.z);
        }
    }
}
