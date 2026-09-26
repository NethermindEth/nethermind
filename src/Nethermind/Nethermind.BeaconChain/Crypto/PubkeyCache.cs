// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Storage;
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
    private const string CountKey = "pubkeys:count";

    private long[] _points = [];

    public int Count { get; private set; }

    /// <summary>Returns the decompressed G1 point of a validator, wrapping the backing buffer without copying.</summary>
    public G1Affine GetPublicKey(int validatorIndex) =>
        new(_points.AsSpan(checked(validatorIndex * G1Affine.Sz), G1Affine.Sz));

    /// <summary>Decompresses all validator pubkeys into a fresh buffer.</summary>
    /// <exception cref="InvalidOperationException">A pubkey is not a valid G1 point, or the sample verification failed.</exception>
    public void Build(Validator[] validators)
    {
        _points = [];
        Count = 0;
        Extend(validators, 0);
    }

    /// <summary>Extends the cache with validators appended to the registry since the last build.</summary>
    /// <param name="validators">The full validator registry.</param>
    /// <param name="fromIndex">The first index that is not yet cached.</param>
    public void Extend(Validator[] validators, int fromIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fromIndex, Count);

        long[] points = new long[checked(validators.Length * G1Affine.Sz)];
        _points.AsSpan(0, fromIndex * G1Affine.Sz).CopyTo(points);

        long firstInvalid = -1;
        Parallel.For(fromIndex, validators.Length, (i, state) =>
        {
            G1Affine point = new(points.AsSpan(i * G1Affine.Sz, G1Affine.Sz));
            BlsPublicKey pubkey = validators[i].Pubkey;
            if (!point.TryDecode(pubkey.Bytes, out Bls.ERROR _))
            {
                Interlocked.CompareExchange(ref firstInvalid, i, -1);
                state.Stop();
            }
        });

        if (firstInvalid >= 0)
        {
            throw new InvalidOperationException($"Validator {firstInvalid} has an invalid BLS public key.");
        }

        _points = points;
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
        Count = count;

        if (!SamplesMatch(validators))
        {
            _points = [];
            Count = 0;
            return false;
        }

        return true;
    }

    private static string ChunkKey(int chunk) => $"pubkeys:{chunk}";

    /// <summary>Re-compresses the first and last cached points and compares them with the registry's compressed pubkeys.</summary>
    private bool SamplesMatch(Validator[] validators) =>
        Count == 0 || (SampleMatches(validators, 0) && SampleMatches(validators, Count - 1));

    private bool SampleMatches(Validator[] validators, int index)
    {
        BlsPublicKey pubkey = validators[index].Pubkey;
        return GetPublicKey(index).Compress().AsSpan().SequenceEqual(pubkey.Bytes);
    }
}
