using System;
using System.IO;

namespace MotionMatching
{
    // Rede totalmente conectada (perceptron multicamadas) usada pelo Learned Motion Matching.
    // Entrada e saída são normalizadas com média/desvio salvos no treino; ativação ReLU ou ELU
    // nas camadas ocultas e saída linear. Os pesos ficam em [entrada][saída] (x * W + b).
    public sealed class Mlp
    {
        public enum Activation { ReLU = 0, ELU = 1 }

        public readonly string Name;
        public readonly Activation Act;
        public readonly int[] Sizes;
        private readonly float[][] weights;
        private readonly float[][] biases;
        private readonly float[] meanIn, stdIn, meanOut, stdOut;
        private readonly float[] bufferA, bufferB;

        public int InputSize => Sizes[0];
        public int OutputSize => Sizes[Sizes.Length - 1];

        public Mlp(BinaryReader reader)
        {
            Name = MotionDatabase.ReadString(reader);
            Act = (Activation)reader.ReadInt32();
            int layers = reader.ReadInt32();
            Sizes = MotionDatabase.ReadInts(reader, layers + 1);
            meanIn = MotionDatabase.ReadFloats(reader, InputSize);
            stdIn = MotionDatabase.ReadFloats(reader, InputSize);
            meanOut = MotionDatabase.ReadFloats(reader, OutputSize);
            stdOut = MotionDatabase.ReadFloats(reader, OutputSize);
            weights = new float[layers][];
            biases = new float[layers][];
            int widest = 0;
            for (int l = 0; l < layers; l++)
            {
                weights[l] = MotionDatabase.ReadFloats(reader, Sizes[l] * Sizes[l + 1]);
                biases[l] = MotionDatabase.ReadFloats(reader, Sizes[l + 1]);
                widest = Math.Max(widest, Math.Max(Sizes[l], Sizes[l + 1]));
            }
            bufferA = new float[widest];
            bufferB = new float[widest];
        }

        public void Evaluate(float[] input, float[] output)
        {
            float[] x = bufferA, y = bufferB;
            for (int i = 0; i < InputSize; i++)
                x[i] = (input[i] - meanIn[i]) / stdIn[i];

            int layers = weights.Length;
            for (int l = 0; l < layers; l++)
            {
                int nin = Sizes[l], nout = Sizes[l + 1];
                float[] w = weights[l];
                Array.Copy(biases[l], y, nout);
                for (int i = 0; i < nin; i++)
                {
                    float xi = x[i];
                    if (xi == 0f)
                        continue; // depois de uma ReLU boa parte das entradas é zero
                    int row = i * nout;
                    int o = 0;
                    for (; o + 3 < nout; o += 4)
                    {
                        y[o] += xi * w[row + o];
                        y[o + 1] += xi * w[row + o + 1];
                        y[o + 2] += xi * w[row + o + 2];
                        y[o + 3] += xi * w[row + o + 3];
                    }
                    for (; o < nout; o++)
                        y[o] += xi * w[row + o];
                }
                if (l < layers - 1)
                {
                    if (Act == Activation.ReLU)
                    {
                        for (int o = 0; o < nout; o++)
                            if (y[o] < 0f) y[o] = 0f;
                    }
                    else
                    {
                        for (int o = 0; o < nout; o++)
                            if (y[o] < 0f) y[o] = (float)Math.Exp(y[o]) - 1f;
                    }
                }
                float[] t = x; x = y; y = t;
            }

            for (int o = 0; o < OutputSize; o++)
                output[o] = x[o] * stdOut[o] + meanOut[o];
        }
    }
}
