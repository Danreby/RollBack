using System;
using System.IO;
using System.Text;

namespace MotionMatching
{
    // Banco de movimento + redes do Learned Motion Matching, lidos do arquivo gerado por
    // Tools/MotionMatching/export_unity.py (TextAsset .bytes na Unity).
    //
    // Osso 0 é a raiz de simulação (projeção do corpo no chão); osso 1 é o quadril, com posição
    // relativa à raiz; os demais só têm rotação local (o comprimento vem do bind do modelo).
    public sealed class MotionDatabase
    {
        public const int Version = 1;

        // Esqueleto
        public int BoneCount;
        public string[] BoneNames;
        public int[] BoneParents;
        public Vec3[] BindLocalPositions;

        // Papéis usados pelo runtime (índices de ossos)
        public int Hips, LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes, RightUpperLeg, RightLowerLeg, RightFoot, RightToes;

        // Frames (60 fps)
        public const float FrameRate = 60f;
        public int FrameCount;
        public Quat[] Rotations;            // [frame * BoneCount + bone], locais
        public Vec3[] AngularVelocities;    // [frame * BoneCount + bone], locais
        public Vec3[] HipsPositions;        // quadril relativo à raiz
        public Vec3[] HipsVelocities;
        public Vec3[] RootVelocities;       // no espaço da raiz
        public Vec3[] RootAngularVelocities;
        public bool[] Contacts;             // [frame * 2 + pé]
        public int[] NextFrame;             // os ciclos voltam ao início

        // Trechos (cada ciclo em uma escala de tempo)
        public int RangeCount;
        public int[] RangeStart, RangeStop, RangeStance, RangeKind;
        public float[] RangeSpeed, RangeScale;
        public int[] FrameRange;

        // Features normalizadas: (bruto - offset) / scale
        public int FeatureCount;
        public float[] Features;            // [frame * FeatureCount + i]
        public float[] FeatureOffset, FeatureScale;
        public string[] FeatureGroupNames;
        public int[] FeatureGroupSizes, FeatureGroupStart;
        public float[] FeatureGroupWeights;
        public int LeftFootPositionFeature, RightFootPositionFeature, LeftFootVelocityFeature, RightFootVelocityFeature,
                   HipVelocityFeature, TrajectoryPositionFeature, TrajectoryDirectionFeature, StanceFeature,
                   RootVelocityFeature, MovingFeature;
        public int[] TrajectoryFrames;      // horizonte da trajetória (frames a 60 fps)

        // Redes do LMM (podem faltar: o modo banco continua funcionando)
        public int LatentCount;
        public Mlp Decompressor, Stepper, Projector;
        public bool HasNetworks => Decompressor != null && Stepper != null && Projector != null;

        public static MotionDatabase Load(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                var db = new MotionDatabase();
                db.Read(reader);
                return db;
            }
        }

        private void Read(BinaryReader r)
        {
            string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
            if (magic != "LMMD")
                throw new InvalidDataException("arquivo de Motion Matching inválido");
            int version = r.ReadInt32();
            if (version != Version)
                throw new InvalidDataException($"versão {version} do banco de movimento não suportada (esperada {Version})");

            BoneCount = r.ReadInt32();
            BoneNames = new string[BoneCount];
            for (int i = 0; i < BoneCount; i++)
                BoneNames[i] = ReadString(r);
            BoneParents = ReadInts(r, BoneCount);
            BindLocalPositions = ReadVec3s(r, BoneCount);

            int roles = r.ReadInt32();
            for (int i = 0; i < roles; i++)
            {
                string role = ReadString(r);
                int bone = r.ReadInt32();
                switch (role)
                {
                    case "Hips": Hips = bone; break;
                    case "LeftUpperLeg": LeftUpperLeg = bone; break;
                    case "LeftLowerLeg": LeftLowerLeg = bone; break;
                    case "LeftFoot": LeftFoot = bone; break;
                    case "LeftToes": LeftToes = bone; break;
                    case "RightUpperLeg": RightUpperLeg = bone; break;
                    case "RightLowerLeg": RightLowerLeg = bone; break;
                    case "RightFoot": RightFoot = bone; break;
                    case "RightToes": RightToes = bone; break;
                }
            }

            FrameCount = r.ReadInt32();
            Rotations = ReadQuats(r, FrameCount * BoneCount);
            AngularVelocities = ReadVec3s(r, FrameCount * BoneCount);
            HipsPositions = ReadVec3s(r, FrameCount);
            HipsVelocities = ReadVec3s(r, FrameCount);
            RootVelocities = ReadVec3s(r, FrameCount);
            RootAngularVelocities = ReadVec3s(r, FrameCount);
            byte[] contacts = r.ReadBytes(FrameCount * 2);
            Contacts = new bool[contacts.Length];
            for (int i = 0; i < contacts.Length; i++)
                Contacts[i] = contacts[i] != 0;
            NextFrame = ReadInts(r, FrameCount);

            RangeCount = r.ReadInt32();
            RangeStart = ReadInts(r, RangeCount);
            RangeStop = ReadInts(r, RangeCount);
            RangeStance = ReadInts(r, RangeCount);
            RangeKind = ReadInts(r, RangeCount);
            RangeSpeed = ReadFloats(r, RangeCount);
            RangeScale = ReadFloats(r, RangeCount);
            FrameRange = new int[FrameCount];
            for (int k = 0; k < RangeCount; k++)
                for (int f = RangeStart[k]; f < RangeStop[k]; f++)
                    FrameRange[f] = k;

            FeatureCount = r.ReadInt32();
            Features = ReadFloats(r, FrameCount * FeatureCount);
            FeatureOffset = ReadFloats(r, FeatureCount);
            FeatureScale = ReadFloats(r, FeatureCount);
            int groups = r.ReadInt32();
            FeatureGroupNames = new string[groups];
            FeatureGroupSizes = new int[groups];
            FeatureGroupStart = new int[groups];
            FeatureGroupWeights = new float[groups];
            int start = 0;
            for (int g = 0; g < groups; g++)
            {
                FeatureGroupNames[g] = ReadString(r);
                FeatureGroupSizes[g] = r.ReadInt32();
                FeatureGroupWeights[g] = r.ReadSingle();
                FeatureGroupStart[g] = start;
                start += FeatureGroupSizes[g];
            }
            int ntraj = r.ReadInt32();
            TrajectoryFrames = ReadInts(r, ntraj);
            LeftFootPositionFeature = GroupStart("left_foot_position");
            RightFootPositionFeature = GroupStart("right_foot_position");
            LeftFootVelocityFeature = GroupStart("left_foot_velocity");
            RightFootVelocityFeature = GroupStart("right_foot_velocity");
            HipVelocityFeature = GroupStart("hip_velocity");
            TrajectoryPositionFeature = GroupStart("trajectory_positions");
            TrajectoryDirectionFeature = GroupStart("trajectory_directions");
            StanceFeature = GroupStart("stance");
            RootVelocityFeature = GroupStart("root_velocity");
            MovingFeature = GroupStart("moving");

            bool networks = r.ReadInt32() != 0;
            if (networks)
            {
                LatentCount = r.ReadInt32();
                Decompressor = new Mlp(r);
                Stepper = new Mlp(r);
                Projector = new Mlp(r);
            }
        }

        public int GroupStart(string name)
        {
            for (int g = 0; g < FeatureGroupNames.Length; g++)
                if (FeatureGroupNames[g] == name)
                    return FeatureGroupStart[g];
            throw new InvalidDataException($"feature '{name}' não encontrada no banco");
        }

        public int FindBone(string name)
        {
            for (int i = 0; i < BoneCount; i++)
                if (BoneNames[i] == name)
                    return i;
            return -1;
        }

        public float NormalizedStance(int stance)
        {
            return (stance - FeatureOffset[StanceFeature]) / FeatureScale[StanceFeature];
        }

        // ---- leitura binária (little endian) ----

        internal static string ReadString(BinaryReader r)
        {
            int n = r.ReadInt32();
            return Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        internal static int[] ReadInts(BinaryReader r, int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadInt32();
            return a;
        }

        internal static float[] ReadFloats(BinaryReader r, int n)
        {
            var a = new float[n];
            byte[] raw = r.ReadBytes(n * 4);
            Buffer.BlockCopy(raw, 0, a, 0, raw.Length);
            if (!BitConverter.IsLittleEndian)
                throw new NotSupportedException("plataforma big-endian não suportada");
            return a;
        }

        internal static Vec3[] ReadVec3s(BinaryReader r, int n)
        {
            float[] f = ReadFloats(r, n * 3);
            var a = new Vec3[n];
            for (int i = 0; i < n; i++) a[i] = new Vec3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
            return a;
        }

        internal static Quat[] ReadQuats(BinaryReader r, int n)
        {
            float[] f = ReadFloats(r, n * 4);
            var a = new Quat[n];
            for (int i = 0; i < n; i++) a[i] = new Quat(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]);
            return a;
        }
    }
}
