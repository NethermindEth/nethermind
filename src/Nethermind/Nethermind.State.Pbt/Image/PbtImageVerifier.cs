// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.Image;

/// <summary>Anchor information obtained from the consumer's chain, never from an artifact.</summary>
/// <param name="MaxBufferedCodeBytes">Local byte budget for whole code plus its 32-byte chunk encoding;
/// not a deployment-code limit. Exhaustion is retryable resource unavailability, not invalid state.</param>
/// <param name="ActivationTimestamp">The chain specification's binaryTrieTime, or null when it schedules none.
/// An anchor must precede it; there is nothing to precede when it is null.</param>
internal sealed record PbtImageAnchor(string ChainId, Hash256 GenesisHash, BlockHeader Header,
    ulong? ActivationTimestamp, int MaxBufferedCodeBytes);

/// <summary>Local buffering budget exhausted; this does not classify an artifact as invalid.</summary>
internal sealed class PbtImageResourceLimitException(string message) : Exception(message);

/// <summary>Verifies an EIP-8347 image into private disk staging, without publishing client state.</summary>
/// <remarks>
/// The snapshot is ordered by PBT key and the preimages by Keccak path, two unrelated orders, so the
/// preimage-driven MPT reconstruction cannot read the leaves where they lie. Rather than seek per leaf,
/// the preimage walk emits one request per leaf it will need, keyed by PBT key and carrying the position
/// it holds in the walk; sorting those and merge-joining them against the snapshot resolves every field
/// in one sequential pass, and re-reading the results by position replays them in preimage order.
/// <para>Leaves that no request claims are the code chunks, which are keyed by code hash rather than by
/// address and so cannot be derived from the preimages. They stay behind in a residual table, small
/// because it holds one copy of each distinct bytecode, which the code reads seek into directly.
/// Requiring the residual to be exactly consumed is what stops a snapshot carrying state the preimages
/// never mention.</para>
/// </remarks>
internal static class PbtImageVerifier
{
    /// <summary>Sort budget for the request and result spools.</summary>
    private const int SortBufferBytes = 128 * 1024 * 1024;

    // Request kinds, in the order the preimage walk emits them for one account.
    private const byte BasicKind = 0;
    private const byte CodeHashKind = 1;
    private const byte DelegationKind = 2;
    private const byte SlotKind = 3;

    private const string RebuildPhase = "PBT verify rebuild";

    private const int SequenceLength = sizeof(ulong);
    private const int SlotCountLength = sizeof(uint);
    private const int LeafLength = 32;

    public static PbtVerifiedImage Verify(Stream snapshot, Stream preimages,
        PbtImageAnchor anchor, string stagingDirectory, ILogManager logManager, CancellationToken cancellationToken = default)
    {
        ValidateAnchor(anchor);
        ILogger logger = logManager.GetClassLogger(typeof(PbtImageVerifier));
        Stopwatch verifying = Stopwatch.StartNew();
        string directory = Path.Combine(stagingDirectory, $"pbt-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string leafPath = Path.Combine(directory, "leaves");
            (ValueHash256 claimedRoot, ulong count) = PbtSnapshotCodec.ReadHeader(snapshot);
            // Snapshot leaves already ascend, so they stream straight into a table with no sort.
            using (ProgressReporter load = PbtImageProgress.Start("PBT verify load", "leaf", count, logManager))
            {
                ulong loaded = 0;
                BuildTable(leafPath, (ref SortedTableBuilder<ArenaBufferWriter> table) =>
                {
                    foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(snapshot, count, cancellationToken))
                    {
                        load.Update(++loaded);
                        table.Add(entry.Key.Bytes, entry.Leaf.Bytes);
                    }
                });
            }
            ValueHash256 root = PbtImageRootCalculator.Calculate(
                Leaves(leafPath, "PBT verify hash", count, logManager, cancellationToken), cancellationToken);
            if (root != claimedRoot) throw new InvalidDataException("PBT snapshot root mismatch.");

            using PbtSortedSpool results = new(directory, SortBufferBytes, writerCount: 1, logManager, cancellationToken);
            string residualPath = Path.Combine(directory, "residual");
            long residualCount;
            using (PbtSortedSpool requests = new(directory, SortBufferBytes, writerCount: 1, logManager, cancellationToken))
            // The result writer closes with the request spool, before the results are read back below.
            using (PbtSortedSpool.Writer resultWriter = results.CreateWriter())
            {
                EmitRequests(preimages, requests, logManager, cancellationToken);
                residualCount = Join(leafPath, requests, resultWriter, residualPath, logManager, cancellationToken);
            }

            string logicalPath = Path.Combine(directory, "logical");
            using (MappedByteFile residual = new(residualPath))
            using (BinaryWriter logical = new(File.Create(logicalPath)))
            {
                CodeTable code = new(residual);
                ValueHash256 mptRoot = PbtImageMptRootCalculator.Calculate(
                    Accounts(results, code, logical, anchor, logManager, cancellationToken), cancellationToken);
                if (mptRoot != anchor.Header.StateRoot!.ValueHash256)
                    throw new InvalidDataException("Snapshot does not reproduce the anchor MPT root.");
                if (code.Consumed != residualCount)
                    throw new InvalidDataException("Snapshot contains leaves not accounted for by its preimages and code.");
            }
            if (logger.IsInfo)
                logger.Info($"PBT verified {count:N0} leaves against root {root} in {verifying.Elapsed:hh\\:mm\\:ss}.");
            return new PbtVerifiedImage(directory, logicalPath, root);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private delegate void TableFill(ref SortedTableBuilder<ArenaBufferWriter> table);

    /// <summary>Stream already-ascending records into one table file.</summary>
    private static void BuildTable(string path, TableFill fill)
    {
        ArenaBufferWriter writer = new(File.Create(path), firstOffset: 0);
        try
        {
            SortedTableBuilder<ArenaBufferWriter> table = new(ref writer);
            try
            {
                fill(ref table);
                table.Build();
            }
            finally { table.Dispose(); }
        }
        finally { writer.Dispose(); }
    }

    private static IEnumerable<RebuildEntry> Leaves(string path, string phase, ulong total, ILogManager logManager,
        CancellationToken cancellationToken)
    {
        ulong read = 0;
        using ProgressReporter progress = PbtImageProgress.Start(phase, "leaf", total, logManager);
        using PbtSortedSpool.Cursor cursor = new([path], cancellationToken);
        while (cursor.MoveNext())
        {
            progress.Update(++read);
            yield return new RebuildEntry(new PbtStorageTreeKey(cursor.Key), new ValueHash256(cursor.Value));
        }
    }

    /// <summary>Emit one request per leaf the preimage walk will need, keyed by its PBT key.</summary>
    /// <remarks>The request carries the walk position so the join's results replay in preimage order,
    /// and the account request also carries the address and slot count the fold cannot otherwise recover.</remarks>
    private static void EmitRequests(Stream preimages, PbtSortedSpool spool, ILogManager logManager,
        CancellationToken cancellationToken)
    {
        using PbtSortedSpool.Writer requests = spool.CreateWriter();
        PbtPreimageReader reader = new(preimages);
        // Sized for the largest payload, a slot key; an account's address and slot count are shorter.
        Span<byte> value = stackalloc byte[SequenceLength + 1 + LeafLength];
        ulong sequence = 0;
        using ProgressReporter progress = PbtImageProgress.Start("PBT verify requests", "req", 0, logManager);
        while (reader.ReadAccount(out Address? address, out uint slots, cancellationToken))
        {
            progress.Update(sequence);
            Address account = address!;
            BinaryPrimitives.WriteUInt64BigEndian(value, sequence++);
            value[SequenceLength] = BasicKind;
            account.Bytes.CopyTo(value[(SequenceLength + 1)..]);
            BinaryPrimitives.WriteUInt32BigEndian(value[(SequenceLength + 1 + Address.Size)..], slots);
            requests.Add(((PbtStorageTreeKey)PbtStateKey.Account(account, BasicKind)).Bytes,
                value[..(SequenceLength + 1 + Address.Size + SlotCountLength)]);

            for (byte kind = CodeHashKind; kind <= DelegationKind; kind++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(value, sequence++);
                value[SequenceLength] = kind;
                requests.Add(((PbtStorageTreeKey)PbtStateKey.Account(account, kind)).Bytes, value[..(SequenceLength + 1)]);
            }

            for (uint index = 0; index < slots; index++)
            {
                progress.Update(sequence);
                ValueHash256 slot = reader.ReadSlot(cancellationToken);
                BinaryPrimitives.WriteUInt64BigEndian(value, sequence++);
                value[SequenceLength] = SlotKind;
                slot.Bytes.CopyTo(value[(SequenceLength + 1)..]);
                requests.Add(PbtStateKey.Storage(account, new UInt256(slot.Bytes, isBigEndian: true)).Bytes,
                    value[..(SequenceLength + 1 + LeafLength)]);
            }
        }
    }

    /// <summary>Merge-join the requests against the snapshot, spilling unclaimed leaves to the residual.</summary>
    /// <returns>The residual leaf count, which the code reads must consume exactly.</returns>
    private static long Join(string leafPath, PbtSortedSpool requests, PbtSortedSpool.Writer results, string residualPath,
        ILogManager logManager, CancellationToken cancellationToken)
    {
        long residualCount = 0;
        ulong joined = 0;
        using ProgressReporter progress = PbtImageProgress.Start("PBT verify join", "req", 0, logManager);
        using PbtSortedSpool.Cursor leaves = new([leafPath], cancellationToken);
        using PbtSortedSpool.Cursor pending = requests.Read();
        BuildTable(residualPath, (ref SortedTableBuilder<ArenaBufferWriter> residual) =>
        {
            // Kind, presence, the largest payload (a slot key) and the leaf it resolved to.
            Span<byte> result = stackalloc byte[2 + LeafLength + LeafLength];
            bool hasLeaf = leaves.MoveNext();
            while (pending.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Update(++joined);
                // Leaves below the request belong to no request at all.
                while (hasLeaf && leaves.Key.SequenceCompareTo(pending.Key) < 0)
                {
                    residual.Add(leaves.Key, leaves.Value);
                    residualCount++;
                    hasLeaf = leaves.MoveNext();
                }
                bool present = hasLeaf && leaves.Key.SequenceEqual(pending.Key);
                ReadOnlySpan<byte> request = pending.Value;
                byte kind = request[SequenceLength];
                ReadOnlySpan<byte> payload = request[(SequenceLength + 1)..];
                result[0] = kind;
                result[1] = present ? (byte)1 : (byte)0;
                payload.CopyTo(result[2..]);
                int length = 2 + payload.Length;
                if (present)
                {
                    leaves.Value.CopyTo(result[length..]);
                    length += LeafLength;
                    hasLeaf = leaves.MoveNext();
                }
                results.Add(request[..SequenceLength], result[..length]);
            }
            while (hasLeaf)
            {
                cancellationToken.ThrowIfCancellationRequested();
                residual.Add(leaves.Key, leaves.Value);
                residualCount++;
                hasLeaf = leaves.MoveNext();
            }
        });
        return residualCount;
    }

    /// <summary>Replay the joined fields in preimage order, rebuilding the MPT the anchor commits to.</summary>
    private static IEnumerable<KeyValuePair<ValueHash256, byte[]>> Accounts(PbtSortedSpool results, CodeTable codes,
        BinaryWriter logical, PbtImageAnchor anchor, ILogManager logManager, CancellationToken cancellationToken)
    {
        ulong rebuilt = 0;
        ulong rebuiltSlots = 0;
        using ProgressReporter progress = PbtImageProgress.Start(RebuildPhase, "acc", 0, logManager);
        progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(RebuildPhase, "acc", p)} | {rebuiltSlots,15:N0} slot");
        using PbtSortedSpool.Cursor cursor = results.Read();
        while (cursor.MoveNext())
        {
            progress.Update(++rebuilt);
            (ValueHash256 basic, Address accountAddress, uint slots) = ReadAccountField(cursor);
            if (basic.Bytes[..4].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("Nonzero basic-data version or reserved bytes.");
            PbtKeyDerivation.UnpackBasicData(basic.Bytes, out ulong nonce, out UInt256 balance);
            uint codeSize = PbtKeyDerivation.ReadBasicDataCodeSize(basic.Bytes);
            if ((ulong)codeSize + ((ulong)codeSize + 30) / 31 * 32 > (ulong)anchor.MaxBufferedCodeBytes)
                throw new PbtImageResourceLimitException("Code verification requires a larger local buffering budget.");

            if (!cursor.MoveNext()) throw new InvalidDataException("Truncated join results.");
            bool hasCodeHash = ReadOptionalField(cursor, CodeHashKind, out ValueHash256 codeHash);
            if (!cursor.MoveNext()) throw new InvalidDataException("Truncated join results.");
            bool hasDelegation = ReadOptionalField(cursor, DelegationKind, out ValueHash256 delegation);
            byte[] code = ReadCode((int)codeSize, hasCodeHash, codeHash, hasDelegation, delegation,
                codes, cancellationToken);
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
                    if (!cursor.MoveNext()) throw new InvalidDataException("Truncated join results.");
                    rebuiltSlots++;
                    (ValueHash256 value, ValueHash256 slot) = ReadSlotField(cursor);
                    logical.Write((byte)2);
                    logical.Write(accountAddress.Bytes);
                    logical.Write(slot.Bytes);
                    logical.Write(value.Bytes);
                    yield return new(ValueKeccak.Compute(slot.Bytes), Rlp.Encode(new UInt256(value.Bytes, isBigEndian: true)).Bytes);
                }
            }
        }
    }

    /// <summary>Split one join result into its payload and, when the snapshot held it, its leaf.</summary>
    /// <remarks>The reader lends its buffer only until the next record, and the fold is an iterator, which
    /// may hold no span across a yield — so every accessor here returns copies.</remarks>
    private static bool Split(PbtSortedSpool.Cursor cursor, byte kind, out ValueHash256 leaf, out int payloadLength)
    {
        ReadOnlySpan<byte> value = cursor.Value;
        if (value[0] != kind) throw new InvalidDataException("Join results out of order.");
        bool present = value[1] != 0;
        payloadLength = value.Length - 2 - (present ? LeafLength : 0);
        leaf = present ? new ValueHash256(value[(2 + payloadLength)..]) : default;
        return present;
    }

    private static (ValueHash256 Basic, Address Address, uint Slots) ReadAccountField(PbtSortedSpool.Cursor cursor)
    {
        if (!Split(cursor, BasicKind, out ValueHash256 basic, out _))
            throw new InvalidDataException("Preimage or required account field has no snapshot leaf.");
        ReadOnlySpan<byte> payload = cursor.Value[2..];
        return (basic, new Address(payload[..Address.Size]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[Address.Size..(Address.Size + SlotCountLength)]));
    }

    private static bool ReadOptionalField(PbtSortedSpool.Cursor cursor, byte kind, out ValueHash256 leaf) =>
        Split(cursor, kind, out leaf, out _);

    private static (ValueHash256 Value, ValueHash256 Slot) ReadSlotField(PbtSortedSpool.Cursor cursor)
    {
        if (!Split(cursor, SlotKind, out ValueHash256 value, out _))
            throw new InvalidDataException("Preimage or required account field has no snapshot leaf.");
        return (value, new ValueHash256(cursor.Value.Slice(2, LeafLength)));
    }

    private static void ValidateAnchor(PbtImageAnchor anchor)
    {
        BlockHeader header = anchor.Header;
        if (anchor.ActivationTimestamp is { } activation && header.Timestamp >= activation || header.Hash is null || header.StateRoot is null)
            throw new InvalidDataException("Image requires a pre-activation MPT anchor.");
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
    }

    private static byte[] ReadCode(int size, bool hasCodeHash, in ValueHash256 codeHashLeaf,
        bool hasDelegation, in ValueHash256 delegation, CodeTable codes, CancellationToken cancellationToken)
    {
        if (hasDelegation)
        {
            if (size != 23 || delegation.Bytes[23..].IndexOfAnyExcept((byte)0) >= 0 ||
                !Eip7702Constants.IsDelegatedCode(delegation.Bytes[..23]) || hasCodeHash)
                throw new InvalidDataException("Invalid delegation header.");
            return delegation.Bytes[..23].ToArray();
        }
        if (!hasCodeHash) throw new InvalidDataException("Preimage or required account field has no snapshot leaf.");
        ValueHash256 codeHash = codeHashLeaf;
        byte[] code = new byte[size];
        int chunks = (int)(((long)size + 30) / 31);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (codes.TryRead(codeHash, chunk, out ValueHash256 value))
                value.Bytes.Slice(1, Math.Min(31, size - chunk * 31)).CopyTo(code.AsSpan(chunk * 31));
        }
        if (ValueKeccak.Compute(code) != codeHash || Eip7702Constants.IsDelegatedCode(code))
            throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
        byte[] encodedChunks = PbtKeyDerivation.ChunkifyCode(code);
        int present = 0;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueHash256 expected = new(encodedChunks.AsSpan(chunk * 32, 32));
            bool stored = codes.TryRead(codeHash, chunk, out ValueHash256 actual);
            if (actual != expected || stored != (expected != default))
                throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
            if (stored) present++;
        }
        codes.Account(codeHash, present);
        return code;
    }

    /// <summary>The leaves no request claimed: one copy of each distinct bytecode, keyed by code hash.</summary>
    /// <remarks>Bytecode is shared between accounts, so <see cref="Consumed"/> counts each chunk once —
    /// comparing it against the residual size is what proves the snapshot carries nothing extra.</remarks>
    private sealed class CodeTable(MappedByteFile residual)
    {
        private readonly Dictionary<ValueHash256, int> _seen = [];

        /// <summary>Distinct residual leaves the code reads have accounted for.</summary>
        public long Consumed { get; private set; }

        public bool TryRead(in ValueHash256 codeHash, int chunk, out ValueHash256 value)
        {
            PbtStorageTreeKey key = (PbtStorageTreeKey)PbtStateKey.Code(codeHash, chunk);
            if (!SortedTableReader.TrySeek<MappedByteFile, NoOpPin>(in residual, new Bound(0, residual.Length), key.Bytes, out Bound found))
            {
                value = default;
                return false;
            }
            Span<byte> bytes = stackalloc byte[LeafLength];
            if (!residual.TryRead(found.Offset, bytes)) throw new InvalidDataException("Truncated residual leaf.");
            value = new ValueHash256(bytes);
            return true;
        }

        /// <summary>Count one bytecode's stored chunks, the first time that bytecode is seen.</summary>
        public void Account(in ValueHash256 codeHash, int storedChunks)
        {
            if (_seen.TryAdd(codeHash, storedChunks)) Consumed += storedChunks;
        }
    }
}

/// <summary>Private verified logical-state spool; callers may stage its contents, then publish separately.</summary>
internal sealed class PbtVerifiedImage(string directory, string logicalPath, ValueHash256 pbtRoot) : IDisposable
{
    private bool _disposed;
    public ValueHash256 PbtRoot { get; } = pbtRoot;

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
