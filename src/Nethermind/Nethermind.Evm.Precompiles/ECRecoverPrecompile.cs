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
using System.Threading;

namespace Nethermind.Evm.Precompiles;

public class ECRecoverPrecompile : IPrecompile<ECRecoverPrecompile>
{
    public static ECRecoverPrecompile Instance { get; } = new();
    private static readonly Result<byte[]> Empty = Array.Empty<byte>();

    private ECRecoverPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(1);

    public static string Name => "ECREC";

    private const int InputLength = 128;

    [ThreadStatic] private static byte[]? t_cachedInput;
    [ThreadStatic] private static Result<byte[]> t_cachedResult;
    private static CachedResult? s_cachedResult;

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
        if (inputData.Length >= InputLength)
        {
            ReadOnlySpan<byte> effectiveInput = inputData.Span[..InputLength];
            if (TryGetCachedResult(effectiveInput, out Result<byte[]> cachedResult))
            {
                return cachedResult;
            }

            Result<byte[]> result = RunInternal(effectiveInput);
            CacheResult(effectiveInput, result);
            return result;
        }

        return RunInternal(inputData);
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

    private static bool TryGetCachedResult(ReadOnlySpan<byte> inputData, out Result<byte[]> result)
    {
        // A non-null t_cachedInput is the sentinel: it is only ever assigned by CacheThreadResult,
        // which fills the buffer and t_cachedResult together, so the two are always in lockstep.
        byte[]? cachedInput = t_cachedInput;
        if (cachedInput is not null && inputData.SequenceEqual(cachedInput))
        {
            result = t_cachedResult;
            return true;
        }

        CachedResult? cachedResult = Volatile.Read(ref s_cachedResult);
        if (cachedResult is not null && cachedResult.Matches(inputData))
        {
            result = cachedResult.Result;
            CacheThreadResult(inputData, result);
            return true;
        }

        result = default;
        return false;
    }

    private static void CacheResult(ReadOnlySpan<byte> inputData, Result<byte[]> result)
    {
        Volatile.Write(ref s_cachedResult, new CachedResult(inputData, result));
        CacheThreadResult(inputData, result);
    }

    private static void CacheThreadResult(ReadOnlySpan<byte> inputData, Result<byte[]> result)
    {
        byte[] cachedInput = t_cachedInput ??= new byte[InputLength];
        inputData.CopyTo(cachedInput);
        t_cachedResult = result;
    }

    private sealed class CachedResult
    {
        private readonly ValueHash256 _message;
        private readonly ValueHash256 _v;
        private readonly ValueHash256 _r;
        private readonly ValueHash256 _s;

        public CachedResult(ReadOnlySpan<byte> inputData, Result<byte[]> result)
        {
            _message = new ValueHash256(inputData[..32]);
            _v = new ValueHash256(inputData.Slice(32, 32));
            _r = new ValueHash256(inputData.Slice(64, 32));
            _s = new ValueHash256(inputData.Slice(96, 32));
            Result = result;
        }

        public Result<byte[]> Result { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(ReadOnlySpan<byte> inputData) =>
            _message.Equals(new ValueHash256(inputData[..32])) &&
            _v.Equals(new ValueHash256(inputData.Slice(32, 32))) &&
            _r.Equals(new ValueHash256(inputData.Slice(64, 32))) &&
            _s.Equals(new ValueHash256(inputData.Slice(96, 32)));
    }
}
