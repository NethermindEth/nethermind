// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.Precompiles;

public class ECRecoverPrecompile : IPrecompile<ECRecoverPrecompile>
{
    public static ECRecoverPrecompile Instance { get; } = new();
    private static readonly Result<byte[]> Empty = Array.Empty<byte>();

    private ECRecoverPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(1);

    public static string Name => "ECREC";

    private const int InputLength = 128;

    [ThreadStatic] private static byte[]? cachedInput;
    [ThreadStatic] private static Result<byte[]> cachedResult;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => 3000UL;

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
        if (inputData.Length < InputLength)
            return RunInternal(inputData);

        ReadOnlySpan<byte> effectiveInput = inputData.Span[..InputLength];
        byte[]? lastInput = cachedInput;
        if (lastInput is not null && effectiveInput.SequenceEqual(lastInput))
            return cachedResult;

        Result<byte[]> result = RunInternal(effectiveInput);

        lastInput ??= cachedInput = new byte[InputLength];
        effectiveInput.CopyTo(lastInput);
        cachedResult = result;
        return result;
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

        int publicKeyLen =
#if ZK_EVM
            64;
#else
            65;
#endif
        Span<byte> publicKey = stackalloc byte[publicKeyLen];

        if (!EthereumEcdsa.RecoverAddressRaw(signature, recoveryId, message, publicKey))
            return Empty;

        byte[] result = new byte[32];
        ref byte resultRef = ref MemoryMarshal.GetArrayDataReference(result);

#if !ZK_EVM
        publicKey = publicKey[1..];
#endif

        KeccakCache.ComputeTo(publicKey, out Unsafe.As<byte, ValueHash256>(ref resultRef));
        // Clear the first 12 bytes, as address is the last 20 bytes of the hash
        Unsafe.InitBlockUnaligned(ref resultRef, 0, 12);

        return result;
    }
}
