// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain;

/// <summary>
/// A prewarm pass in which an uncached call to an expensive precompile returns a placeholder at once while the real
/// result is computed on the thread pool into the shared precompile cache, so the calls whose inputs do not depend on
/// an earlier precompile's result are computed side by side instead of one after another.
/// </summary>
/// <remarks>Only the thread that opened a pass sees placeholders; every other caller, the main thread included, runs
/// precompiles as usual and finds the results the pass scheduled once they land.</remarks>
public static class PrecompileLookahead
{
    [ThreadStatic] private static int t_depth;
    [ThreadStatic] private static int t_issued;
    private static readonly ConcurrentDictionary<PrecompileCaches.Key, byte> s_inFlight = new();

    private static readonly byte[] True32 = One32();

    // FIELD_ELEMENTS_PER_BLOB and the BLS modulus, the point evaluation precompile's success output.
    private static readonly byte[] KzgSuccess = Convert.FromHexString(
        "0000000000000000000000000000000000000000000000000000000000001000" +
        "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001");

    public static bool Active => t_depth > 0;

    /// <summary>Placeholders handed out since the current pass began.</summary>
    public static int Issued => t_issued;

    public static Pass Begin()
    {
        t_depth++;
        t_issued = 0;
        return default;
    }

    public readonly struct Pass : IDisposable
    {
        public void Dispose() => t_depth--;
    }

    private static byte[] One32()
    {
        byte[] value = new byte[32];
        value[31] = 1;
        return value;
    }

    /// <summary>The placeholder an expensive precompile returns in a pass; the cheap ones run as usual.</summary>
    internal static bool TryPlaceholder(Address address, ReadOnlySpan<byte> input, out Result<byte[]> placeholder)
    {
        byte[]? output = address.PrecompileIndexOrNegative() switch
        {
            // ecrecover is answered for real: contracts check the signer at once, and a placeholder signer ends the pass.
            0x05 => ModExpOutput(input),
            0x07 => new byte[64],
            0x08 => True32,
            0x0a => KzgSuccess,
            0x0b or 0x0c or 0x10 => new byte[128],
            0x0d or 0x0e or 0x11 => new byte[256],
            0x0f => True32,
            0x100 => True32,
            _ => null
        };

        placeholder = output ?? default(Result<byte[]>);
        return output is not null;
    }

    private static byte[]? ModExpOutput(ReadOnlySpan<byte> input)
    {
        // The modulus length is the third 32-byte word; past the EIP-7823 bound the call fails fast anyway.
        if (input.Length < 96 || input[64..88].IndexOfAnyExcept((byte)0) >= 0) return null;
        ulong length = BinaryPrimitives.ReadUInt64BigEndian(input[88..96]);
        return length is 0 or > 1024 ? null : new byte[length];
    }

    /// <summary>Computes the call on the thread pool into <paramref name="cache"/>, once per input.</summary>
    internal static void Schedule(IPrecompile precompile, Address address, ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> effectiveInput,
        IReleaseSpec spec, PrecompileCaches.Partition cache)
    {
        t_issued++;
        PrecompileCaches.Key key = new(address, effectiveInput.ToArray(), spec);
        if (!s_inFlight.TryAdd(key, 0)) return;

        ThreadPool.UnsafeQueueUserWorkItem(static job =>
        {
            try
            {
                Result<byte[]> result = job.Precompile.Run(job.Data, job.Spec);
                if (result is not { IsError: true, Error: Errors.InvalidInputLength }) job.Cache.TryAdd(job.Key, result);
            }
            catch (Exception)
            {
                // Speculative: the caller that needs the result computes it itself.
            }
            finally
            {
                s_inFlight.TryRemove(job.Key, out _);
            }
        }, new Job(precompile, input.ToArray(), spec, cache, key), preferLocal: false);
    }

    private sealed record Job(IPrecompile Precompile, byte[] Data, IReleaseSpec Spec, PrecompileCaches.Partition Cache, PrecompileCaches.Key Key);
}
