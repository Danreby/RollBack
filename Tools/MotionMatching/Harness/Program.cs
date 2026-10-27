using System;
using System.Collections.Generic;
using System.IO;
using MotionMatching;

// Roda cenários de input no núcleo do Motion Matching (o mesmo código usado na Unity) e grava as
// poses para análise (analyze.py). Uso: dotnet run -- <banco .bytes> <pasta de saída> [modo] [dt]
//   modo: learned | database | both (padrão)   dt: fixo (ex.: 0.016667) ou "random" (30-144 fps)
public static class Program
{
    const float WalkSpeed = 2.4f, RunSpeed = 7.0f, SprintSpeed = 12.5f, StrafeSpeed = 4.8f;
    static float velocityHalflife = 0.15f, rotationHalflife = 0.12f;

    // Um trecho do cenário: direção do direcional (mundo), velocidade e postura
    struct Segment
    {
        public float duration, angleDeg, speed, spinDegPerSec;
        public int stance;
        public bool external;
        public float? faceDeg;   // mira travada: encara esta direção e anda para qualquer lado
        public Segment(float duration, float angleDeg, float speed, int stance = 0, float spin = 0f, bool external = false, float? face = null)
        {
            this.duration = duration; this.angleDeg = angleDeg; this.speed = speed; this.stance = stance;
            spinDegPerSec = spin; this.external = external; faceDeg = face;
        }
    }

    static Dictionary<string, Segment[]> Scenarios()
    {
        return new Dictionary<string, Segment[]>
        {
            ["walk_run_stop"] = new[] {
                new Segment(1.0f, 0, 0), new Segment(2.5f, 0, WalkSpeed), new Segment(3f, 0, RunSpeed),
                new Segment(2f, 0, SprintSpeed), new Segment(2f, 0, 0), new Segment(2f, 0, WalkSpeed), new Segment(1.5f, 0, 0) },
            ["turns"] = new[] {
                new Segment(0.5f, 0, 0), new Segment(2f, 0, RunSpeed), new Segment(2f, 90, RunSpeed), new Segment(2f, -90, RunSpeed),
                new Segment(4f, -90, RunSpeed, 0, 120f), new Segment(2f, 0, WalkSpeed), new Segment(2f, 180, WalkSpeed),
                new Segment(1.5f, 180, 0) },
            ["stance"] = new[] {
                new Segment(1f, 0, 0), new Segment(1f, 0, 0, 1), new Segment(2f, 0, WalkSpeed, 1), new Segment(2.5f, 0, RunSpeed, 1),
                new Segment(2f, 0, RunSpeed, 0), new Segment(1.5f, 0, 0, 0), new Segment(1.5f, 0, 0, 1) },
            ["stop_go"] = new[] {
                new Segment(0.5f, 0, 0), new Segment(0.4f, 0, RunSpeed), new Segment(0.4f, 0, 0), new Segment(0.6f, 90, RunSpeed),
                new Segment(0.3f, 90, 0), new Segment(0.8f, -45, WalkSpeed), new Segment(0.5f, 0, 0), new Segment(1.0f, 180, SprintSpeed),
                new Segment(1.5f, 0, 0) },
            ["lockon"] = new[] {
                new Segment(0.5f, 0, 0, 0, 0, false, 0), new Segment(1.5f, -90, StrafeSpeed, 0, 0, false, 0),
                new Segment(1.5f, 90, StrafeSpeed, 0, 0, false, 0), new Segment(1.2f, 180, StrafeSpeed, 0, 0, false, 0),
                new Segment(1.2f, 45, StrafeSpeed, 0, 0, false, 0), new Segment(1.2f, -135, WalkSpeed, 0, 0, false, 0),
                new Segment(1.0f, 0, 0, 0, 0, false, 0),
                new Segment(1.5f, -90, StrafeSpeed, 1, 0, false, 0), new Segment(1.5f, 180, StrafeSpeed, 1, 0, false, 0),
                new Segment(1.5f, 90, StrafeSpeed, 2, 0, false, 0), new Segment(1.5f, 180, WalkSpeed, 2, 0, false, 0),
                new Segment(1.0f, 0, 0, 2, 0, false, 0) },
            ["stance2"] = new[] {
                new Segment(1f, 0, 0, 2), new Segment(2f, 0, WalkSpeed, 2), new Segment(2.5f, 0, RunSpeed, 2),
                new Segment(2f, 0, SprintSpeed, 2), new Segment(2f, 90, RunSpeed, 2), new Segment(1.5f, 90, 0, 2),
                new Segment(2f, 0, SprintSpeed, 1), new Segment(1.5f, 0, 0, 1) },
            ["external"] = new[] {
                new Segment(0.5f, 0, 0), new Segment(2f, 0, RunSpeed), new Segment(0.8f, 0, 0, 0, 0, true),
                new Segment(2f, 0, RunSpeed), new Segment(1.5f, 0, 0) },
        };
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("uso: Harness <banco.bytes> <saida> [learned|database|both] [dt|random]");
            return 1;
        }
        var db = MotionDatabase.Load(File.ReadAllBytes(args[0]));
        if (args[1] == "--verify")
            return Verify(db, args[2]);
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        string modes = args.Length > 2 ? args[2] : "both";
        string dtArg = args.Length > 3 ? args[3] : "0.0166667";
        if (args.Length > 4) velocityHalflife = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        if (args.Length > 5) rotationHalflife = float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"banco: {db.FrameCount} frames, {db.BoneCount} ossos, {db.FeatureCount} features, redes: {db.HasNetworks}");

        foreach (var kv in Scenarios())
            foreach (string mode in new[] { "learned", "database" })
            {
                if (modes != "both" && modes != mode) continue;
                if (mode == "learned" && !db.HasNetworks) continue;
                Run(db, kv.Key, kv.Value, mode == "learned", dtArg, Path.Combine(outDir, $"{kv.Key}_{mode}.bin"));
            }
        return 0;
    }

    // Compara a inferência em C# com a do treino (arquivo gerado por verify_networks.py):
    // para cada rede, entradas e saídas esperadas; para o decompressor, também as rotações decodificadas.
    static int Verify(MotionDatabase db, string path)
    {
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            float worst = 0f;
            foreach (var net in new[] { db.Decompressor, db.Stepper, db.Projector })
            {
                int n = r.ReadInt32(), nin = r.ReadInt32(), nout = r.ReadInt32();
                float maxErr = 0f, maxRot = 0f;
                var x = new float[nin];
                var y = new float[nout];
                for (int k = 0; k < n; k++)
                {
                    for (int i = 0; i < nin; i++) x[i] = r.ReadSingle();
                    net.Evaluate(x, y);
                    for (int i = 0; i < nout; i++)
                        maxErr = Math.Max(maxErr, Math.Abs(y[i] - r.ReadSingle()));
                    if (net == db.Decompressor)
                    {
                        for (int b = 1; b < db.BoneCount; b++)
                        {
                            Quat q = MotionMatcher.DecodeRotation(y, 3 + (b - 1) * 6);
                            var e = new Quat(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            maxRot = Math.Max(maxRot, 1f - Math.Abs(Quat.Dot(q, e)));
                        }
                    }
                }
                Console.WriteLine($"{net.Name,-13} {n} amostras: maior diferença {maxErr:E2}" + (net == db.Decompressor ? $", rotações 1-|dot| {maxRot:E2}" : ""));
                worst = Math.Max(worst, maxErr);
            }
            return worst < 1e-3f ? 0 : 2;
        }
    }

    static void Run(MotionDatabase db, string name, Segment[] segments, bool learned, string dtArg, string path)
    {
        var matcher = new MotionMatcher(db) { UseLearned = learned };
        var sim = new LocomotionSimulation { VelocityHalflife = velocityHalflife, RotationHalflife = rotationHalflife };
        var state = new SimulationState();
        var external = new MotionPose(db.BoneCount);
        float[] times = new float[3];
        for (int k = 0; k < 3; k++) times[k] = db.TrajectoryFrames[k] / MotionDatabase.FrameRate;
        var rng = new Random(7);
        Quat desiredRotation = Quat.Identity;

        // pose "externa" (substitui o Animator): idle armado, frame fixo
        int ext = db.RangeStart[Array.FindIndex(db.RangeStance, s => s == 1)];
        for (int i = 1; i < db.BoneCount; i++) external.Rotations[i] = db.Rotations[ext * db.BoneCount + i];
        external.HipsPosition = db.HipsPositions[ext];

        var frames = new List<float[]>();
        float total = 0f;
        foreach (var s in segments) total += s.duration;
        float t = 0f;
        long ticks = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (t < total)
        {
            float dt = dtArg == "random" ? (float)(1.0 / (30 + rng.NextDouble() * 114)) : float.Parse(dtArg, System.Globalization.CultureInfo.InvariantCulture);
            float acc = 0f;
            Segment seg = segments[segments.Length - 1];
            float local = 0f;
            foreach (var s in segments)
            {
                if (t < acc + s.duration) { seg = s; local = t - acc; break; }
                acc += s.duration;
            }
            float angle = (seg.angleDeg + seg.spinDegPerSec * local) * (float)Math.PI / 180f;
            Vec3 desiredVelocity = new Vec3((float)Math.Sin(angle), 0f, (float)Math.Cos(angle)) * seg.speed;
            if (seg.external) desiredVelocity = Vec3.Zero;
            if (seg.faceDeg.HasValue)
                desiredRotation = Quat.FromYaw(seg.faceDeg.Value * (float)Math.PI / 180f);
            else if (desiredVelocity.Length > 0.01f)
                desiredRotation = Quat.YawFromDirection(desiredVelocity);

            sim.Step(desiredVelocity, desiredRotation, dt);
            state.Position = sim.Position;
            state.Rotation = sim.Rotation;
            state.Velocity = sim.Velocity;
            state.DesiredVelocity = desiredVelocity;
            state.DesiredRotation = desiredRotation;
            sim.Predict(desiredVelocity, desiredRotation, times, state.TrajectoryPositions, state.TrajectoryRotations);

            matcher.Update(dt, state, seg.stance, !seg.external, external);
            ticks++;

            var row = new List<float> { t, dt };
            row.AddRange(new[] { sim.Position.x, sim.Position.y, sim.Position.z });
            row.AddRange(new[] { sim.Rotation.x, sim.Rotation.y, sim.Rotation.z, sim.Rotation.w });
            row.AddRange(new[] { matcher.RootOffset.x, matcher.RootOffset.y, matcher.RootOffset.z });
            row.Add((float)matcher.CurrentSource);
            row.Add(matcher.CurrentFrame);
            row.Add(matcher.LeftFootLocked ? 1 : 0);
            row.Add(matcher.RightFootLocked ? 1 : 0);
            row.Add(matcher.LeftFootContact ? 1 : 0);
            row.Add(matcher.RightFootContact ? 1 : 0);
            row.Add(matcher.LeftFootStepping ? 1 : 0);
            row.Add(matcher.RightFootStepping ? 1 : 0);
            row.Add(matcher.LastSearchCost);
            row.Add(seg.speed);
            row.Add(seg.stance);
            row.AddRange(new[] { matcher.Output.HipsPosition.x, matcher.Output.HipsPosition.y, matcher.Output.HipsPosition.z });
            for (int i = 0; i < db.BoneCount; i++)
            {
                Quat q = matcher.Output.Rotations[i];
                row.AddRange(new[] { q.x, q.y, q.z, q.w });
            }
            var anim = matcher.AnimatedPose;
            row.AddRange(new[] { anim.HipsPosition.x, anim.HipsPosition.y, anim.HipsPosition.z });
            for (int i = 0; i < db.BoneCount; i++)
            {
                Quat q = anim.Rotations[i];
                row.AddRange(new[] { q.x, q.y, q.z, q.w });
            }
            frames.Add(row.ToArray());
            t += dt;
        }
        watch.Stop();

        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(frames.Count);
            w.Write(frames[0].Length);
            foreach (var row in frames)
                foreach (float v in row) w.Write(v);
        }
        Console.WriteLine($"{Path.GetFileName(path),-28} {frames.Count,5} frames, {matcher.TransitionCount,3} transições, " +
                          $"{watch.Elapsed.TotalMilliseconds * 1000.0 / ticks:F0} us/frame");
    }
}
