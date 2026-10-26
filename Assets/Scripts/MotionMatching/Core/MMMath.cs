using System;

// Matemática do Motion Matching sem dependência da Unity (o mesmo código roda nos testes fora do
// editor). Vec3/Quat seguem as convenções da Vector3/Quaternion da Unity: produto de Hamilton,
// q * v gira v, eixos da Unity (Y para cima, Z para frente).
namespace MotionMatching
{
    public struct Vec3
    {
        public float x, y, z;

        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.x, -a.y, -a.z); }
        public static Vec3 operator *(Vec3 a, float s) { return new Vec3(a.x * s, a.y * s, a.z * s); }
        public static Vec3 operator *(float s, Vec3 a) { return new Vec3(a.x * s, a.y * s, a.z * s); }
        public static Vec3 operator /(Vec3 a, float s) { return new Vec3(a.x / s, a.y / s, a.z / s); }

        public static float Dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        }

        public float Length => (float)Math.Sqrt(x * x + y * y + z * z);
        public float SqrLength => x * x + y * y + z * z;

        public Vec3 Normalized(float eps = 1e-8f)
        {
            float l = Length;
            return l > eps ? this / l : Zero;
        }

        public Vec3 Planar => new Vec3(x, 0f, z);

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) { return a + (b - a) * t; }

        public override string ToString() { return $"({x:F3}, {y:F3}, {z:F3})"; }
    }

    public struct Quat
    {
        public float x, y, z, w;

        public Quat(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);

        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        public static Vec3 operator *(Quat q, Vec3 v)
        {
            Vec3 u = new Vec3(q.x, q.y, q.z);
            Vec3 t = 2f * Vec3.Cross(u, v);
            return v + q.w * t + Vec3.Cross(u, t);
        }

        public Quat Inverse => new Quat(-x, -y, -z, w);

        // Mesma rotação no hemisfério com w >= 0 (caminho mais curto)
        public Quat Abs => w < 0f ? new Quat(-x, -y, -z, -w) : this;

        public Quat Normalized
        {
            get
            {
                float l = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                return l > 1e-8f ? new Quat(x / l, y / l, z / l, w / l) : Identity;
            }
        }

        public static float Dot(Quat a, Quat b) { return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w; }

        public static Quat Exp(Vec3 v, float eps = 1e-8f)
        {
            float halfangle = v.Length;
            if (halfangle < eps)
                return new Quat(v.x, v.y, v.z, 1f).Normalized;
            float c = (float)Math.Cos(halfangle);
            float s = (float)Math.Sin(halfangle) / halfangle;
            return new Quat(s * v.x, s * v.y, s * v.z, c);
        }

        public static Vec3 Log(Quat q, float eps = 1e-8f)
        {
            float length = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (length < eps)
                return new Vec3(q.x, q.y, q.z);
            float halfangle = (float)Math.Atan2(length, q.w);
            return halfangle * (new Vec3(q.x, q.y, q.z) / length);
        }

        // Ângulo * eixo (radianos), a forma usada pelas molas e pela inertialização
        public static Vec3 ToScaledAngleAxis(Quat q) { return 2f * Log(q); }
        public static Quat FromScaledAngleAxis(Vec3 v) { return Exp(v * 0.5f); }

        public static Quat FromAngleAxis(float radians, Vec3 axis)
        {
            return FromScaledAngleAxis(axis.Normalized() * radians);
        }

        public static Quat Slerp(Quat a, Quat b, float t)
        {
            if (Dot(a, b) < 0f)
                b = new Quat(-b.x, -b.y, -b.z, -b.w);
            Vec3 delta = ToScaledAngleAxis((b * a.Inverse).Abs);
            return (FromScaledAngleAxis(delta * t) * a).Normalized;
        }

        // Rotação que leva +Z para 'forward' (com +Y para cima), como Quaternion.LookRotation
        public static Quat YawFromDirection(Vec3 forward)
        {
            float yaw = (float)Math.Atan2(forward.x, forward.z);
            return new Quat(0f, (float)Math.Sin(yaw * 0.5f), 0f, (float)Math.Cos(yaw * 0.5f));
        }

        public static Quat FromYaw(float yaw)
        {
            return new Quat(0f, (float)Math.Sin(yaw * 0.5f), 0f, (float)Math.Cos(yaw * 0.5f));
        }

        // Matriz a partir das duas primeiras colunas (representação contínua usada pela rede)
        public static Quat FromTwoColumns(float c0x, float c0y, float c0z, float c1x, float c1y, float c1z)
        {
            Vec3 a = new Vec3(c0x, c0y, c0z).Normalized();
            Vec3 c = Vec3.Cross(a, new Vec3(c1x, c1y, c1z)).Normalized();
            Vec3 b = Vec3.Cross(c, a);
            return FromMatrix(a.x, b.x, c.x, a.y, b.y, c.y, a.z, b.z, c.z);
        }

        public static Quat FromMatrix(float m00, float m01, float m02, float m10, float m11, float m12,
                                      float m20, float m21, float m22)
        {
            float t = m00 + m11 + m22;
            Quat q;
            if (t > 0f)
            {
                float s = (float)Math.Sqrt(t + 1f) * 2f;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        public override string ToString() { return $"({x:F4}, {y:F4}, {z:F4}, {w:F4})"; }
    }

    // Molas criticamente amortecidas em forma fechada (Daniel Holden, "Spring-It-On").
    // "halflife" é o tempo para cobrir metade da distância até o alvo.
    public static class Springs
    {
        public const float Ln2 = 0.69314718056f;

        public static float HalflifeToDamping(float halflife, float eps = 1e-5f)
        {
            return (4f * Ln2) / (halflife + eps);
        }

        public static float NegExp(float x)
        {
            return (float)Math.Exp(-x);
        }

        // Leva x (e a velocidade v) até zero
        public static void DecaySpringDamperExact(ref Vec3 x, ref Vec3 v, float halflife, float dt)
        {
            float y = HalflifeToDamping(halflife) / 2f;
            Vec3 j1 = v + x * y;
            float eydt = NegExp(y * dt);
            x = eydt * (x + j1 * dt);
            v = eydt * (v - j1 * y * dt);
        }

        public static void DecaySpringDamperExact(ref Quat x, ref Vec3 v, float halflife, float dt)
        {
            float y = HalflifeToDamping(halflife) / 2f;
            Vec3 j0 = Quat.ToScaledAngleAxis(x);
            Vec3 j1 = v + j0 * y;
            float eydt = NegExp(y * dt);
            x = Quat.FromScaledAngleAxis(eydt * (j0 + j1 * dt));
            v = eydt * (v - j1 * y * dt);
        }

        // Rotação seguindo um alvo
        public static void SimpleSpringDamperExact(ref Quat x, ref Vec3 v, Quat goal, float halflife, float dt)
        {
            float y = HalflifeToDamping(halflife) / 2f;
            Vec3 j0 = Quat.ToScaledAngleAxis((x * goal.Inverse).Abs);
            Vec3 j1 = v + j0 * y;
            float eydt = NegExp(y * dt);
            x = Quat.FromScaledAngleAxis(eydt * (j0 + j1 * dt)) * goal;
            v = eydt * (v - j1 * y * dt);
        }

        // Posição/velocidade/aceleração indo para a velocidade desejada (molas da simulação do personagem)
        public static void SimulationPositionsUpdate(ref Vec3 position, ref Vec3 velocity, ref Vec3 acceleration,
                                                     Vec3 desiredVelocity, float halflife, float dt)
        {
            float y = HalflifeToDamping(halflife) / 2f;
            Vec3 j0 = velocity - desiredVelocity;
            Vec3 j1 = acceleration + j0 * y;
            float eydt = NegExp(y * dt);
            Vec3 prev = position;
            position = eydt * ((-j1) / (y * y) + (-j0 - j1 * dt) / y)
                       + j1 / (y * y) + j0 / y + desiredVelocity * dt + prev;
            velocity = eydt * (j0 + j1 * dt) + desiredVelocity;
            acceleration = eydt * (acceleration - j1 * y * dt);
        }

        // Ajuste exponencial: fração de 'g' a percorrer em dt
        public static Vec3 DampAdjustmentExact(Vec3 g, float halflife, float dt, float eps = 1e-8f)
        {
            return g * (1f - NegExp((Ln2 * dt) / (halflife + eps)));
        }

        public static Quat DampAdjustmentExact(Quat g, float halflife, float dt, float eps = 1e-8f)
        {
            return Quat.Slerp(Quat.Identity, g, 1f - NegExp((Ln2 * dt) / (halflife + eps)));
        }
    }
}
