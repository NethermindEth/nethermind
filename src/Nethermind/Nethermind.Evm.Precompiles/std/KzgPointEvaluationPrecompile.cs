// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.Evm.Precompiles;

public partial class KzgPointEvaluationPrecompile
{
    // The top three bits of a compressed BLS12-381 point: compression, infinity and the sign of y.
    private const byte FlagsMask = 0b1110_0000;
    private const byte Uncompressed = 0b0000_0000;
    private const byte UncompressedInfinity = 0b0100_0000;
    private const byte Compressed = 0b1000_0000;
    private const byte CompressedWithSign = 0b1010_0000;
    private const byte CompressedInfinity = 0b1100_0000;
    private const string SubgroupCheckFailed = "invalid point: subgroup check failed";

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.KzgPointEvaluationPrecompile++;

        return RunInternal(inputData);
    }

    /// <remarks>
    /// The reason is go-ethereum's, which reports what its KZG library rejects first: a claimed value or evaluation
    /// point that is not a canonical scalar, then a commitment or proof that does not decode to a G1 subgroup point,
    /// in gnark-crypto's words, and otherwise the proof itself.
    /// </remarks>
    private static partial string DescribeFailedVerification(ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> proof)
    {
        string reason = IsCanonicalScalar(y) && IsCanonicalScalar(z)
            ? DescribeInvalidPoint(commitment) ?? DescribeInvalidPoint(proof) ?? "can't verify opening proof"
            : "scalar is not canonical when interpreted as a big integer in big-endian";

        return $"error verifying kzg proof: {reason}";
    }

    private static bool IsCanonicalScalar(ReadOnlySpan<byte> scalar) => scalar.SequenceCompareTo(_successResult.AsSpan(32)) < 0;

    /// <returns>Why a 48-byte compressed G1 point does not decode to a subgroup point, or <c>null</c> when it does.</returns>
    [SkipLocalsInit]
    private static string? DescribeInvalidPoint(ReadOnlySpan<byte> point)
    {
        switch (point[0] & FlagsMask)
        {
            case Uncompressed or UncompressedInfinity:
                // flags of the 96-byte uncompressed form, which the 48 bytes are too short for
                return "short buffer";
            case CompressedInfinity:
                return (point[0] & ~FlagsMask) == 0 && !point[1..].ContainsAnyExcept((byte)0) ? null : "invalid infinity point encoding";
            case Compressed or CompressedWithSign:
                break;
            default:
                return "invalid point encoding";
        }

        Span<byte> x = stackalloc byte[Eip2537.LenFp];
        x[..Eip2537.LenFpPad].Clear();
        point.CopyTo(x[Eip2537.LenFpPad..]);
        x[Eip2537.LenFpPad] &= unchecked((byte)~FlagsMask);
        Result canonical = Eip2537.ValidRawFp(x);
        if (!canonical)
            return canonical.Error;

        G1Affine decoded = new(stackalloc long[G1Affine.Sz]);
        if (!decoded.TryDecode(point, out Bls.ERROR error))
        {
            return error switch
            {
                Bls.ERROR.POINTNOTONCURVE => "invalid compressed coordinate: square root doesn't exist",
                Bls.ERROR.POINTNOTINGROUP => SubgroupCheckFailed,
                _ => "invalid point encoding",
            };
        }

        return decoded.InGroup() ? null : SubgroupCheckFailed;
    }
}
