// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile : IPrecompile<ECRecoverPrecompile>
{
    public static ECRecoverPrecompile Instance { get; } = new();
    private static readonly Result<byte[]> Empty = Array.Empty<byte>();

    private ECRecoverPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(1);

    public static string Name => "ECREC";

    private const int InputLength = 128;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3000L;

    // RunInternal zero-pads short inputs to InputLength, so trailing zeros are insignificant.
    // Trimming them normalizes e.g. a 64-byte input and its 128-byte zero-padded equivalent to the same key.
    public ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData)
    {
        ReadOnlyMemory<byte> clamped = inputData.Length > InputLength ? inputData[..InputLength] : inputData;
        int end = clamped.Span.LastIndexOfAnyExcept((byte)0);
        return end < 0 ? ReadOnlyMemory<byte>.Empty : clamped[..(end + 1)];
    }

    private readonly byte[] _zero31 = new byte[31];

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
#if !ZK_EVM
        Metrics.ECRecoverPrecompile++;
#endif
        return inputData.Length >= 128 ? RunInternal(inputData.Span) : RunInternal(inputData);
    }

    private Result<byte[]> RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[InputLength];
        inputData.Span[..Math.Min(InputLength, inputData.Length)]
            .CopyTo(inputDataSpan[..Math.Min(InputLength, inputData.Length)]);

        return RunInternal(inputDataSpan);
    }

    private Result<byte[]> RunInternal(ReadOnlySpan<byte> inputDataSpan)
    {
        ReadOnlySpan<byte> vBytes = inputDataSpan.Slice(32, 32);

        if (!Bytes.AreEqual(_zero31, vBytes[..31]))
            return Empty;

        byte v = vBytes[31];

        if (v != 27 && v != 28)
            return Empty;

        ReadOnlySpan<byte> message = inputDataSpan[..32];
        ReadOnlySpan<byte> signature = inputDataSpan.Slice(64, 64);
        byte recoveryId = Signature.GetRecoveryId(v);

        return Recover(signature, recoveryId, message);
    }
}
