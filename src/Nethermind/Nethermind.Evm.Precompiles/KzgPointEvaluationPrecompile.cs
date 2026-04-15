// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-4844#point-evaluation-precompile" />
/// </summary>
public partial class KzgPointEvaluationPrecompile : IPrecompile<KzgPointEvaluationPrecompile>
{
    public static readonly KzgPointEvaluationPrecompile Instance = new();

    // FIELD_ELEMENTS_PER_BLOB and BLS_MODULUS as padded 32 byte big endian values
    private static readonly byte[] _successResult = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 16, 0, 115, 237, 167, 83, 41, 157, 125, 72, 51, 57, 216, 8, 9, 161, 216, 5, 83, 189, 164, 2, 255, 254, 91, 254, 255, 255, 255, 255, 0, 0, 0, 1];

    public static Address Address { get; } = Address.FromNumber(0x0a);

    public static string Name => "KZG_POINT_EVALUATION";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 50_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Result<byte[]> RunInternal(ReadOnlyMemory<byte> inputData)
    {
        if (inputData.Length != 192)
            return Errors.Failed;

        ReadOnlySpan<byte> inputSpan = inputData.Span;
        ReadOnlySpan<byte> versionedHash = inputSpan[..32];
        ReadOnlySpan<byte> z = inputSpan[32..64];
        ReadOnlySpan<byte> y = inputSpan[64..96];
        ReadOnlySpan<byte> commitment = inputSpan[96..144];
        ReadOnlySpan<byte> proof = inputSpan[144..192];
        Span<byte> hash = stackalloc byte[32];

        bool success = KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitment, hash) &&
            hash.SequenceEqual(versionedHash) &&
            KzgPolynomialCommitments.VerifyProof(commitment, z, y, proof);

        return success ? _successResult : Errors.Failed;
    }
}
