// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Threading;
using Nethermind.BeaconChain.Types;
using Nethermind.Crypto;
using Snappier;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>Maps validator index to a decompressed BLS G1 public key point.</summary>
/// <remarks>
/// Decompressing ~2M validator pubkeys takes minutes, so it is done once (parallelized) and the
/// raw point buffer is persisted to the store's metadata column in snappy-compressed chunks of
/// <see cref="ValidatorsPerChunk"/> validators. The buffer uses platform-local endianness, which
/// is fine for a node-local cache; a format byte guards against incompatible layouts.
/// </remarks>
public class PubkeyCache
{
    private const byte FormatVersion = 1;
    private const int ValidatorsPerChunk = 65_536;

    private const int ParallelCheckThreshold = 64;
    internal const string CountKey = "pubkeys:count";

    private long[] _points = [];

    // Per validator: 0 not yet checked, 1 in G1, 2 outside G1.
    private byte[] _subgroupChecks = [];

    // Held while Extend copies and replaces the verdicts, so a warm-up that ends then either sees the replacement or had its verdicts copied.
    private readonly Lock _subgroupChecksSwap = new();

    public int Count { get; private set; }

    /// <summary>Called by <see cref="Extend"/> once the new keys are decoded, before it publishes them; lets a test place a warm-up there.</summary>
    internal Action? ExtensionDecoded { get; set; }

    /// <summary>Called by <see cref="Extend"/> inside the swap lock after copying the verdicts, before it publishes them; lets a test place a warm-up there.</summary>
    internal Action? ExtensionChecksCopied { get; set; }

    /// <summary>Called by <see cref="WarmSubgroupChecks"/> at the start of each pass over the verdicts it has read; lets a test extend the cache mid-pass.</summary>
    internal Action? WarmUpPassStarted { get; set; }

    /// <summary><c>blst_p1s_add(ret, points[], npoints)</c>, which the managed wrapper does not bind; resolved from the library it loads.</summary>
    private static readonly unsafe delegate* unmanaged<long*, long**, nuint, void> BatchAdd =
        (delegate* unmanaged<long*, long**, nuint, void>)NativeLibrary.GetExport(NativeLibrary.Load("blst", typeof(Bls).Assembly, null), "blst_p1s_add");

    /// <summary>Returns the decompressed G1 point of a validator, wrapping the backing buffer without copying.</summary>
    public G1Affine GetPublicKey(int validatorIndex) =>
        new(_points.AsSpan(checked(validatorIndex * G1Affine.Sz), G1Affine.Sz));

    /// <summary>Returns whether a validator's cached public key is in the prime-order subgroup G1.</summary>
    /// <remarks>
    /// The subgroup check is made on first use and remembered, as it costs several times the decompression the build
    /// already pays. Concurrent callers may both check the same key and store the same result.
    /// </remarks>
    internal bool IsInSubgroup(int validatorIndex)
    {
        byte[] checks = _subgroupChecks;
        byte check = checks[validatorIndex];
        if (check == 0)
            checks[validatorIndex] = check = GetPublicKey(validatorIndex).InGroup() ? (byte)1 : (byte)2;
        return check == 1;
    }

    /// <summary>Remembers the subgroup check of every cached key that has none yet, so block imports do not pay for them.</summary>
    /// <remarks>
    /// Fans out over at most half the processors so the importer keeps the rest. Safe beside <see cref="IsInSubgroup"/>, which stores the same
    /// verdict, and beside <see cref="Extend"/>: a verdict is only ever stored for the key it was computed from. An <see cref="Extend"/>
    /// that replaces the verdict array during the run makes it check the replacement again, as verdicts stored in the old array are lost.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Cancelled; the verdicts stored so far stay.</exception>
    internal virtual void WarmSubgroupChecks(CancellationToken cancellationToken)
    {
        byte[] checks;
        do
        {
            // Extend stores the points before the checks, so reading them in the opposite order never pairs new checks with old points.
            checks = Volatile.Read(ref _subgroupChecks);
            long[] points = Volatile.Read(ref _points);
            WarmUpPassStarted?.Invoke();
            BeaconParallel.For(0, Math.Min(checks.Length, points.Length / G1Affine.Sz), Math.Max(1, Environment.ProcessorCount / 2), cancellationToken, i =>
            {
                if (checks[i] == 0)
                    checks[i] = new G1Affine(points.AsSpan(i * G1Affine.Sz, G1Affine.Sz)).InGroup() ? (byte)1 : (byte)2;
            });
        }
        while (!IsCurrent(checks));
    }

    private bool IsCurrent(byte[] checks)
    {
        lock (_subgroupChecksSwap)
        {
            return ReferenceEquals(checks, _subgroupChecks);
        }
    }

    /// <summary>Whether a validator's subgroup check is already remembered.</summary>
    internal bool HasSubgroupCheck(int validatorIndex)
    {
        byte[] checks = _subgroupChecks;
        return (uint)validatorIndex < (uint)checks.Length && checks[validatorIndex] != 0;
    }

    /// <summary>Returns a validator's cached public key and whether it is in the prime-order subgroup G1, which <c>KeyValidate</c> requires of every key a signature is verified under.</summary>
    /// <remarks>Unreachable from a registry whose keys were all validated on entry; refusing here keeps a key that slipped in from verifying signatures.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The index is not cached.</exception>
    internal bool TryGetValidPublicKey(int validatorIndex, out G1Affine key)
    {
        key = GetPublicKey(validatorIndex);
        return IsInSubgroup(validatorIndex);
    }

    /// <summary>Writes the sum of the public keys of <paramref name="validatorIndices"/> into <paramref name="sum"/> as <see cref="SumPublicKeys"/> does, unless one of them is outside G1.</summary>
    /// <returns><c>false</c> when a key is not in the prime-order subgroup: a sum over it is not <c>KeyValidate</c>d key material, so <paramref name="sum"/> is then unspecified.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An index is not cached, or <paramref name="sum"/> is not a G1 point buffer.</exception>
    internal bool TrySumValidPublicKeys(ReadOnlySpan<ulong> validatorIndices, Span<long> sum)
    {
        CheckUncheckedInParallel(validatorIndices);
        foreach (ulong index in validatorIndices)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, (ulong)Count, nameof(validatorIndices));
            if (!IsInSubgroup((int)index))
                return false;
        }

        SumPublicKeys(validatorIndices, sum);
        return true;
    }

    private void CheckUncheckedInParallel(ReadOnlySpan<ulong> validatorIndices)
    {
        // Spec BLS KeyValidate: each verdict must match its point; Extend publishes points before verdicts.
        byte[] checks = Volatile.Read(ref _subgroupChecks);
        long[] points = Volatile.Read(ref _points);
        List<int>? pending = null;
        foreach (ulong index in validatorIndices)
        {
            if (index < (ulong)checks.Length && checks[(int)index] == 0)
                (pending ??= []).Add((int)index);
        }

        if (pending is null || pending.Count < ParallelCheckThreshold)
            return;

        BeaconParallel.ForEach(pending, index =>
        {
            if (checks[index] == 0)
                checks[index] = new G1Affine(points.AsSpan(index * G1Affine.Sz, G1Affine.Sz)).InGroup() ? (byte)1 : (byte)2;
        });
    }

    /// <summary>Writes the sum of the public keys of <paramref name="validatorIndices"/> into <paramref name="sum"/>, a Jacobian G1 point.</summary>
    /// <remarks>
    /// One batched affine addition shares a single field inversion across the batch; adding the keys one call at a time
    /// costs a full point addition plus a native transition each, which is seconds per block for slot-wide Electra
    /// aggregates on a registry of a million validators. The indices must be distinct.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">An index is not cached, or <paramref name="sum"/> is not a G1 point buffer.</exception>
    public unsafe void SumPublicKeys(ReadOnlySpan<ulong> validatorIndices, Span<long> sum)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(sum.Length, Bls.P1.Sz);
        long[] points = _points;
        ulong count = (ulong)(points.Length / G1Affine.Sz);
        nint[] addresses = ArrayPool<nint>.Shared.Rent(Math.Max(validatorIndices.Length, 1));
        try
        {
            // The pointers are only dereferenced by the native call below, while points stays pinned.
            fixed (long* pointsBase = points)
            fixed (nint* addressesBase = addresses)
            fixed (long* sumBase = sum)
            {
                for (int i = 0; i < validatorIndices.Length; i++)
                {
                    ulong index = validatorIndices[i];
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(validatorIndices));
                    addresses[i] = (nint)(pointsBase + (long)index * G1Affine.Sz);
                }

                sum.Clear();
                if (validatorIndices.Length > 0)
                {
                    BatchAdd(sumBase, (long**)addressesBase, (nuint)validatorIndices.Length);
                }
            }
        }
        finally
        {
            ArrayPool<nint>.Shared.Return(addresses);
        }
    }

    /// <summary>Decompresses all validator pubkeys into a fresh buffer.</summary>
    /// <exception cref="InvalidOperationException">A pubkey does not decode or is the point at infinity, or the sample verification failed.</exception>
    public void Build(Validator[] validators)
    {
        _points = [];
        Count = 0;
        Extend(validators, 0);
    }

    /// <summary>Extends the cache with validators appended to the registry since the last build.</summary>
    /// <param name="validators">The full validator registry.</param>
    /// <param name="fromIndex">The first index that is not yet cached.</param>
    /// <exception cref="InvalidOperationException">A pubkey does not decode or is the point at infinity, or the sample verification failed.</exception>
    public void Extend(Validator[] validators, int fromIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fromIndex, Count);

        long[] points = new long[checked(validators.Length * G1Affine.Sz)];
        _points.AsSpan(0, fromIndex * G1Affine.Sz).CopyTo(points);
        byte[] subgroupChecks = new byte[validators.Length];

        long firstInvalid = -1;
        BeaconParallel.For(fromIndex, validators.Length, (i, state) =>
        {
            G1Affine point = new(points.AsSpan(i * G1Affine.Sz, G1Affine.Sz));
            BlsPublicKey pubkey = validators[i].Pubkey;
            // KeyValidate refuses infinity in every verification that sums these keys; the subgroup check is deferred to IsInSubgroup.
            if (!point.TryDecode(pubkey.Bytes, out Bls.ERROR _) || point.IsInf())
            {
                Interlocked.CompareExchange(ref firstInvalid, i, -1);
                state.Stop();
            }
        });

        if (firstInvalid >= 0)
        {
            throw new InvalidOperationException($"Validator {firstInvalid} has an invalid BLS public key.");
        }

        ExtensionDecoded?.Invoke();
        lock (_subgroupChecksSwap)
        {
            _subgroupChecks.AsSpan(0, fromIndex).CopyTo(subgroupChecks);
            ExtensionChecksCopied?.Invoke();
            Volatile.Write(ref _points, points);
            Volatile.Write(ref _subgroupChecks, subgroupChecks);
        }

        Count = validators.Length;

        if (!SamplesMatch(validators))
        {
            throw new InvalidOperationException("Pubkey cache sample verification failed after build.");
        }
    }

    /// <summary>Persists the point buffer to the store. The count entry is written last so an interrupted persist is not loadable.</summary>
    public void Persist(BeaconChainStore store)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_points.AsSpan());
        for (int chunk = 0; chunk * ValidatorsPerChunk < Count; chunk++)
        {
            int offset = chunk * ValidatorsPerChunk * G1Affine.Sz * sizeof(long);
            store.PutMetadata(ChunkKey(chunk), Snappy.CompressToArray(bytes.Slice(offset, Math.Min(ValidatorsPerChunk * G1Affine.Sz * sizeof(long), bytes.Length - offset))));
        }

        byte[] countValue = new byte[1 + sizeof(int)];
        countValue[0] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(countValue.AsSpan(1), Count);
        store.PutMetadata(CountKey, countValue);
    }

    /// <summary>Loads a persisted cache matching the given registry, verifying a sample of entries.</summary>
    /// <returns><c>false</c> when nothing usable is persisted (missing, format/count mismatch, or corrupt) — rebuild instead.</returns>
    public bool TryLoad(BeaconChainStore store, Validator[] validators)
    {
        byte[]? countValue = store.GetMetadata(CountKey);
        if (countValue is null || countValue.Length != 1 + sizeof(int) || countValue[0] != FormatVersion)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(countValue.AsSpan(1));
        if (count != validators.Length)
        {
            return false;
        }

        long[] points = new long[checked(count * G1Affine.Sz)];
        Span<byte> bytes = MemoryMarshal.AsBytes(points.AsSpan());
        for (int chunk = 0; chunk * ValidatorsPerChunk < count; chunk++)
        {
            byte[]? compressed = store.GetMetadata(ChunkKey(chunk));
            int offset = chunk * ValidatorsPerChunk * G1Affine.Sz * sizeof(long);
            int expected = Math.Min(ValidatorsPerChunk * G1Affine.Sz * sizeof(long), bytes.Length - offset);
            if (compressed is null
                || Snappy.GetUncompressedLength(compressed) != expected
                || Snappy.Decompress(compressed, bytes.Slice(offset, expected)) != expected)
            {
                return false;
            }
        }

        _points = points;
        _subgroupChecks = new byte[count];
        Count = count;

        if (!SamplesMatch(validators) || HoldsInfinity())
        {
            _points = [];
            _subgroupChecks = [];
            Count = 0;
            return false;
        }

        return true;
    }

    private static string ChunkKey(int chunk) => $"pubkeys:{chunk}";

    /// <summary>Re-compresses the first and last cached points and compares them with the registry's compressed pubkeys.</summary>
    private bool SamplesMatch(Validator[] validators) =>
        Count == 0 || (SampleMatches(validators, 0) && SampleMatches(validators, Count - 1));

    /// <summary>Whether a loaded point is the point at infinity, which <see cref="Extend"/> never caches but a persisted buffer may hold.</summary>
    private bool HoldsInfinity()
    {
        for (int i = 0; i < Count; i++)
        {
            if (GetPublicKey(i).IsInf())
                return true;
        }

        return false;
    }

    private bool SampleMatches(Validator[] validators, int index)
    {
        BlsPublicKey pubkey = validators[index].Pubkey;
        return GetPublicKey(index).Compress().AsSpan().SequenceEqual(pubkey.Bytes);
    }
}
