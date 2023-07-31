// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

public class PointEvaluationPrecompile : IPrecompile
{
    public static readonly IPrecompile Instance = new PointEvaluationPrecompile();

    private static readonly ReadOnlyMemory<byte> PointEvaluationSuccessfulResponse =
        ((UInt256)Ckzg.Ckzg.FieldElementsPerBlob).ToBigEndian()
        .Concat(KzgPolynomialCommitments.BlsModulus.ToBigEndian())
        .ToArray();

    public static Address Address { get; } = Address.FromNumber(0x0a);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 50000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsValid(in ReadOnlyMemory<byte> inputData)
        {
            if (inputData.Length != 192)
            {
                return false;
            }

            ReadOnlySpan<byte> inputDataSpan = inputData.Span;
            ReadOnlySpan<byte> versionedHash = inputDataSpan[..32];
            ReadOnlySpan<byte> z = inputDataSpan[32..64];
            ReadOnlySpan<byte> y = inputDataSpan[64..96];
            ReadOnlySpan<byte> commitment = inputDataSpan[96..144];
            ReadOnlySpan<byte> proof = inputDataSpan[144..192];
            Span<byte> hash = stackalloc byte[32];

            return KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitment, hash)
                   && hash.SequenceEqual(versionedHash)
                   && KzgPolynomialCommitments.VerifyProof(commitment, z, y, proof);
        }

        Metrics.PointEvaluationPrecompile++;
        return IsValid(inputData)
            ? (PointEvaluationSuccessfulResponse, true)
            : (default, false);
    }
}
