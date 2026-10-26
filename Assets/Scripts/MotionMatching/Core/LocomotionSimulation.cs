namespace MotionMatching
{
    // "Simulação" do personagem: um ponto no chão cuja velocidade e rotação seguem o input por
    // molas criticamente amortecidas. É ela que a cápsula de colisão segue; a animação vem depois e
    // procura no banco o movimento que reproduz a trajetória que a simulação promete.
    // Por ser em forma fechada, a mesma mola prevê exatamente onde o personagem estará daqui a
    // 1/3, 2/3 e 1 segundo (a trajetória futura usada na busca).
    public sealed class LocomotionSimulation
    {
        public Vec3 Position;
        public Vec3 Velocity;
        public Vec3 Acceleration;
        public Quat Rotation = Quat.Identity;
        public Vec3 AngularVelocity;

        public float VelocityHalflife = 0.15f;
        public float RotationHalflife = 0.12f;

        public void Step(Vec3 desiredVelocity, Quat desiredRotation, float dt)
        {
            Springs.SimulationPositionsUpdate(ref Position, ref Velocity, ref Acceleration, desiredVelocity, VelocityHalflife, dt);
            Springs.SimpleSpringDamperExact(ref Rotation, ref AngularVelocity, desiredRotation, RotationHalflife, dt);
        }

        // Posições/rotações daqui a times[i] segundos mantendo o input atual
        public void Predict(Vec3 desiredVelocity, Quat desiredRotation, float[] times, Vec3[] positions, Quat[] rotations)
        {
            for (int i = 0; i < times.Length; i++)
            {
                Vec3 p = Position, v = Velocity, a = Acceleration;
                Springs.SimulationPositionsUpdate(ref p, ref v, ref a, desiredVelocity, VelocityHalflife, times[i]);
                Quat r = Rotation;
                Vec3 w = AngularVelocity;
                Springs.SimpleSpringDamperExact(ref r, ref w, desiredRotation, RotationHalflife, times[i]);
                positions[i] = p;
                rotations[i] = r;
            }
        }
    }
}
