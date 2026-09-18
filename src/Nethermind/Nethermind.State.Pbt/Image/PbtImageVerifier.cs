// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Pbt.Image;

/// <summary>Anchor information obtained from the consumer's chain, never from an artifact.</summary>
/// <param name="MaxBufferedCodeBytes">Local byte budget for whole code plus its 32-byte chunk encoding;
/// not a deployment-code limit. Exhaustion is retryable resource unavailability, not invalid state.</param>
internal sealed record PbtImageAnchor(string ChainId, Hash256 GenesisHash, BlockHeader Header,
    bool IsFinalized, ulong ActivationTimestamp, int MaxBufferedCodeBytes);

/// <summary>Local buffering budget exhausted; this does not classify an artifact as invalid.</summary>
internal sealed class PbtImageResourceLimitException(string message) : Exception(message);

/// <summary>Verifies an EIP-8347 image into private disk staging, without publishing client state.</summary>
internal static class PbtImageVerifier
{
    public static PbtVerifiedImage Verify(Stream snapshot, Stream preimages, PbtArtifactIdentity identity,
        PbtImageAnchor anchor, string stagingDirectory, CancellationToken cancellationToken = default)
    {
        ValidateAnchor(identity, anchor);
        string directory = Path.Combine(stagingDirectory, $"pbt-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using LeafSpool leaves = new(Path.Combine(directory, "leaves"));
            (ValueHash256 claimedRoot, ulong count) = PbtSnapshotCodec.ReadHeader(snapshot);
            foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(snapshot, count, cancellationToken)) leaves.Add(entry);
            ValueHash256 root = PbtImageRootCalculator.Calculate(leaves.Enumerate(cancellationToken), cancellationToken);
            if (root != claimedRoot) throw new InvalidDataException("PBT snapshot root mismatch.");
            string logicalPath = Path.Combine(directory, "logical");
            using (BinaryWriter logical = new(File.Create(logicalPath)))
            {
                PbtPreimageReader reader = new(preimages);
                ValueHash256 mptRoot = PbtImageMptRootCalculator.Calculate(Accounts(), cancellationToken);
                if (mptRoot != anchor.Header.StateRoot!.ValueHash256)
                    throw new InvalidDataException("Snapshot does not reproduce the anchor MPT root.");
                leaves.RequireAllConsumed(cancellationToken);

                IEnumerable<KeyValuePair<ValueHash256, byte[]>> Accounts()
                {
                    while (reader.ReadAccount(out Address? address, out uint slots, cancellationToken))
                    {
                        Address accountAddress = address!;
                        ValueHash256 basic = leaves.Required((PbtStorageTreeKey)PbtStateKey.Account(accountAddress, 0));
                        if (basic.Bytes[..4].IndexOfAnyExcept((byte)0) >= 0)
                            throw new InvalidDataException("Nonzero basic-data version or reserved bytes.");
                        PbtKeyDerivation.UnpackBasicData(basic.Bytes, out ulong nonce, out UInt256 balance);
                        uint codeSize = PbtKeyDerivation.ReadBasicDataCodeSize(basic.Bytes);
                        if ((ulong)codeSize + ((ulong)codeSize + 30) / 31 * 32 > (ulong)anchor.MaxBufferedCodeBytes)
                            throw new PbtImageResourceLimitException("Code verification requires a larger local buffering budget.");
                        byte[] code = ReadCode(accountAddress, (int)codeSize, leaves, cancellationToken);
                        if (nonce == 0 && balance.IsZero && code.Length == 0)
                            throw new InvalidDataException("Empty account violates EIP-7523.");
                        ValueHash256 storageRoot = PbtImageMptRootCalculator.Calculate(Storage(), cancellationToken);
                        Account account = new(nonce, balance, storageRoot.ToHash256(), Keccak.Compute(code));
                        byte[] encoded = AccountDecoder.Instance.Encode(account).Bytes;
                        logical.Write((byte)1);
                        logical.Write(accountAddress.Bytes);
                        logical.Write(encoded.Length);
                        logical.Write(encoded);
                        logical.Write(code.Length);
                        logical.Write(code);
                        yield return new(ValueKeccak.Compute(accountAddress.Bytes), encoded);

                        IEnumerable<KeyValuePair<ValueHash256, byte[]>> Storage()
                        {
                            for (uint index = 0; index < slots; index++)
                            {
                                ValueHash256 slot = reader.ReadSlot(cancellationToken);
                                ValueHash256 value = leaves.Required(PbtStateKey.Storage(accountAddress, new UInt256(slot.Bytes, isBigEndian: true)));
                                logical.Write((byte)2);
                                logical.Write(accountAddress.Bytes);
                                logical.Write(slot.Bytes);
                                logical.Write(value.Bytes);
                                yield return new(ValueKeccak.Compute(slot.Bytes), Rlp.Encode(new UInt256(value.Bytes, isBigEndian: true)).Bytes);
                            }
                        }
                    }
                }
            }
            return new PbtVerifiedImage(directory, logicalPath, root, anchor.Header.StateRoot!.ValueHash256);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static void ValidateAnchor(PbtArtifactIdentity identity, PbtImageAnchor anchor)
    {
        BlockHeader header = anchor.Header;
        if (!anchor.IsFinalized || header.Timestamp >= anchor.ActivationTimestamp || header.Hash is null || header.StateRoot is null)
            throw new InvalidDataException("Image requires a finalized pre-activation MPT anchor.");
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
        if (identity.ChainId != anchor.ChainId || identity.AnchorNumber != header.Number ||
            !string.Equals(identity.GenesisHash, anchor.GenesisHash.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.AnchorHash, header.Hash.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.AnchorMptRoot, header.StateRoot.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact identity does not match the consumer's chain anchor.");
    }

    private static byte[] ReadCode(Address address, int size, LeafSpool leaves, CancellationToken cancellationToken)
    {
        PbtStorageTreeKey hashKey = (PbtStorageTreeKey)PbtStateKey.Account(address, 1);
        PbtStorageTreeKey delegationKey = (PbtStorageTreeKey)PbtStateKey.Account(address, 2);
        if (leaves.TryRead(delegationKey, out ValueHash256 delegation))
        {
            if (size != 23 || delegation.Bytes[23..].IndexOfAnyExcept((byte)0) >= 0 ||
                !Eip7702Constants.IsDelegatedCode(delegation.Bytes[..23]) || leaves.TryRead(hashKey, out _))
                throw new InvalidDataException("Invalid delegation header.");
            return delegation.Bytes[..23].ToArray();
        }
        ValueHash256 codeHash = leaves.Required(hashKey);
        byte[] code = new byte[size];
        int chunks = (int)(((long)size + 30) / 31);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leaves.TryRead((PbtStorageTreeKey)PbtStateKey.Code(address, codeHash, chunk), out ValueHash256 value))
                value.Bytes.Slice(1, Math.Min(31, size - chunk * 31)).CopyTo(code.AsSpan(chunk * 31));
        }
        if (ValueKeccak.Compute(code) != codeHash || Eip7702Constants.IsDelegatedCode(code))
            throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
        byte[] encodedChunks = PbtKeyDerivation.ChunkifyCode(code);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueHash256 expected = new(encodedChunks.AsSpan(chunk * 32, 32));
            bool present = leaves.TryRead((PbtStorageTreeKey)PbtStateKey.Code(address, codeHash, chunk), out ValueHash256 actual);
            if (actual != expected || present != (expected != default))
                throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
        }
        return code;
    }

    /// <summary>Fixed-width sorted disk index with one on-disk consumption bit per leaf.</summary>
    private sealed class LeafSpool(string path) : IDisposable
    {
        private const int RecordSize = 100;
        private readonly FileStream _stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        private long _count;

        public void Add(RebuildEntry entry)
        {
            Span<byte> record = stackalloc byte[RecordSize];
            record.Clear();
            record[0] = (byte)entry.Key.Length;
            entry.Key.Bytes.CopyTo(record[1..]);
            entry.Leaf.Bytes.CopyTo(record[67..]);
            _stream.Write(record);
            _count++;
        }

        public bool TryRead(in PbtStorageTreeKey key, out ValueHash256 value)
        {
            Span<byte> record = stackalloc byte[RecordSize];
            long low = 0, high = _count - 1;
            while (low <= high)
            {
                long middle = low + (high - low) / 2;
                _stream.Position = checked(middle * RecordSize);
                _stream.ReadExactly(record);
                int comparison = record.Slice(1, record[0]).SequenceCompareTo(key.Bytes);
                if (comparison < 0) low = middle + 1;
                else if (comparison > 0) high = middle - 1;
                else
                {
                    value = new ValueHash256(record.Slice(67, 32));
                    _stream.Position = checked(middle * RecordSize + 99);
                    _stream.WriteByte(1);
                    return true;
                }
            }
            value = default;
            return false;
        }

        public ValueHash256 Required(in PbtStorageTreeKey key) => TryRead(key, out ValueHash256 value)
            ? value : throw new InvalidDataException("Preimage or required account field has no snapshot leaf.");

        public IEnumerable<RebuildEntry> Enumerate(CancellationToken cancellationToken)
        {
            byte[] record = new byte[RecordSize];
            for (long index = 0; index < _count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _stream.Position = checked(index * RecordSize);
                _stream.ReadExactly(record);
                yield return new(new PbtStorageTreeKey(record.AsSpan(1, record[0])), new ValueHash256(record.AsSpan(67, 32)));
            }
        }

        public void RequireAllConsumed(CancellationToken cancellationToken)
        {
            for (long index = 0; index < _count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _stream.Position = checked(index * RecordSize + 99);
                if (_stream.ReadByte() != 1) throw new InvalidDataException("Snapshot contains leaves not accounted for by its preimages and code.");
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}

/// <summary>Private verified logical-state spool; callers may stage its contents, then publish separately.</summary>
internal sealed class PbtVerifiedImage(string directory, string logicalPath, ValueHash256 pbtRoot, ValueHash256 mptRoot) : IDisposable
{
    private bool _disposed;
    public ValueHash256 PbtRoot { get; } = pbtRoot;
    public ValueHash256 MptRoot { get; } = mptRoot;

    /// <summary>Streams verified logical records into caller-owned staging; storage precedes its account record.</summary>
    /// <remarks>Callbacks must not publish live state. Whole-code buffering follows the verified local budget;
    /// a future streaming code sink is needed to remove that resource limitation.</remarks>
    public void Replay(Action<Address, Account, byte[]> account, Action<Address, UInt256, ValueHash256> storage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using BinaryReader reader = new(File.OpenRead(logicalPath));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte kind = reader.ReadByte();
            Address address = new(reader.ReadBytes(20));
            if (kind == 2)
            {
                UInt256 slot = new(reader.ReadBytes(32), isBigEndian: true);
                storage(address, slot, new ValueHash256(reader.ReadBytes(32)));
            }
            else
            {
                byte[] encoded = reader.ReadBytes(reader.ReadInt32());
                RlpReader decoder = new(encoded);
                Account decoded = AccountDecoder.Instance.Decode(ref decoder)!;
                account(address, decoded, reader.ReadBytes(reader.ReadInt32()));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Directory.Delete(directory, recursive: true);
    }
}
