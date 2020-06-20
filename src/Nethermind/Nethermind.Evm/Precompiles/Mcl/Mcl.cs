using System;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Mcl
{
    public class Mcl
    {
        public static UInt256 ReadScalar(in Span<byte> inputDataSpan, in int offset)
        {
            Span<byte> s = inputDataSpan.Slice(offset, 32);
            UInt256.CreateFromBigEndian(out UInt256 scalar, s);
            return scalar;
        }
        
        public static void PrepareInputData(byte[] inputData, Span<byte> preparedData)
        {
            inputData ??= Bytes.Empty;
            inputData.AsSpan(0, Math.Min(preparedData.Length, inputData.Length))
                .CopyTo(preparedData.Slice(0, Math.Min(preparedData.Length, inputData.Length)));
        }
    }
}