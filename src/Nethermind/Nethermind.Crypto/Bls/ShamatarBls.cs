using System;
using System.Runtime.InteropServices;

namespace Nethermind.Crypto.Bls
{
    public static class ShamatarLib
    {
        [DllImport("runtimes\\win-x64\\native\\eth_196.dll")]
        private static extern unsafe uint eip196_perform_operation(
            byte operation,
            byte* input,
            int inputLength,
            byte* output,
            ref int outputLength,
            byte* error,
            ref int errorLength);

        [DllImport("runtimes\\win-x64\\native\\eip_pairings2537.dll")]
        private static extern unsafe uint eip2537_perform_operation(
            byte operation,
            byte* input,
            int inputLength,
            byte* output,
            ref int outputLength,
            byte* error,
            ref int errorLength);

        private static unsafe bool Bn256Op(byte operation, Span<byte> input, Span<byte> output)
        {
            int outputLength = output.Length;
            int errorLength = 256;
            uint externalCallResult;

            Span<byte> error = stackalloc byte[errorLength];
            fixed (byte* inputPtr = &MemoryMarshal.GetReference(input))
            fixed (byte* outputPtr = &MemoryMarshal.GetReference(output))
            fixed (byte* errorPtr = &MemoryMarshal.GetReference(error))
            {
                externalCallResult = eip196_perform_operation(
                    operation, inputPtr, input.Length, outputPtr, ref outputLength, errorPtr, ref errorLength);
            }

            return externalCallResult == 0;
        }

        public static bool Bn256Add(Span<byte> input, Span<byte> output)
        {
            return Bn256Op(1, input, output);
        }

        public static bool Bn256Mul(Span<byte> input, Span<byte> output)
        {
            return Bn256Op(2, input, output);
        }

        public static bool Bn256Pairing(Span<byte> input, Span<byte> output)
        {
            return Bn256Op(3, input, output);
        }
    }
}