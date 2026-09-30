// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
/// <remarks>
/// Leaves carry no storage roots, so accounts are staged with the empty tree root; PBT never treats a storage root as
/// authoritative. Code chunks are keyed by code hash rather than by account, so they are tabled as they stream past
/// and each bytecode is reassembled once the whole stream, and with it every code size, has been seen. Requiring the
/// reassembled code to consume every tabled chunk is what stops a stream carrying code no account names.
/// </remarks>
internal static class PbtLeafStaging
{
    private const byte AccountZone = 0x00;
    private const byte CodeZone = 0x01;
    private const int DelegationLength = 23;
    private const int HeaderStorageSlots = 64;

    public static (ulong Accounts, ulong Slots) Stage(PbtAnchorPublication.LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
        string scratchDirectory, int maxBufferedCodeBytes, CancellationToken cancellationToken)
    {
        string chunkPath = Path.Combine(scratchDirectory, $"pbt-code-{Guid.NewGuid():N}");
        Dictionary<ValueHash256, int> codeSizes = [];
        ulong accounts = 0;
        ulong slots = 0;
        long chunkCount = 0;
        IPbtPersistence.IReader? stagedState = null;
        try
        {
            BuildTable(chunkPath, (ref SortedTableBuilder<ArenaBufferWriter> chunks) =>
            {
                ValueHash256? stem = null;
                ValueHash256 basicData = default;
                ValueHash256? codeHash = null;
                ValueHash256? delegation = null;
                PbtStorageTreeKey? runKey = null;
                ISlotRun run = SlotRun.Empty;
                ValueHash256? storageOwner = null;

                foreach (RebuildEntry entry in leaves)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> key = entry.Key.Bytes;
                    if (key[0] == CodeZone)
                    {
                        FlushAccount();
                        chunks.Add(key, entry.Leaf.Bytes);
                        chunkCount++;
                        continue;
                    }
                    if (key[0] == AccountZone)
                    {
                        ValueHash256 addressHash = new(key[1..33]);
                        if (stem != addressHash)
                        {
                            FlushAccount();
                            stem = addressHash;
                        }
                        switch (key[^1])
                        {
                            case PbtKeyDerivation.BasicDataLeafKey:
                                basicData = entry.Leaf;
                                continue;
                            case PbtKeyDerivation.CodeHashLeafKey:
                                codeHash = entry.Leaf;
                                continue;
                            case PbtKeyDerivation.DelegationLeafKey:
                                delegation = entry.Leaf;
                                continue;
                            case < PbtKeyDerivation.HeaderStorageOffset or >= PbtKeyDerivation.HeaderStorageOffset + HeaderStorageSlots:
                                throw new InvalidDataException("Snapshot holds a leaf at a reserved account sub-index.");
                        }
                    }
                    else
                    {
                        FlushAccount();
                        ValueHash256 owner = new(key[1..33]);
                        if (storageOwner != owner)
                        {
                            // Every header precedes every storage leaf, so the staged state already holds the owner.
                            stagedState ??= batch.CreateReader();
                            if (stagedState.GetAccount(owner) is null)
                                throw new InvalidDataException("Snapshot holds storage of an account it has no header for.");
                            storageOwner = owner;
                        }
                    }

                    slots++;
                    PbtStorageTreeKey slotRunKey = SlotRun.RunKey(entry.Key);
                    if (runKey != slotRunKey) FlushRun();
                    runKey = slotRunKey;
                    ISlotRun previous = run;
                    run = previous.With(SlotRun.IndexOf(entry.Key), EvmWordSlot.FromStripped(entry.Leaf.Bytes));
                    SlotRun.Return(previous);
                }
                FlushAccount();
                FlushRun();

                void FlushAccount()
                {
                    if (stem is not { } addressHash) return;
                    if (basicData.Bytes[..4].IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidDataException("Nonzero basic-data version or reserved bytes.");
                    int codeSize = (int)PbtKeyDerivation.ReadBasicDataCodeSize(basicData.Bytes);
                    PbtKeyDerivation.UnpackBasicData(basicData.Bytes, out ulong nonce, out UInt256 balance);
                    if (nonce == 0 && balance.IsZero && codeSize == 0)
                        throw new InvalidDataException("Empty account violates EIP-7523.");

                    ValueHash256 accountCodeHash;
                    if (delegation is { } delegated)
                    {
                        if (codeSize != DelegationLength || codeHash is not null || delegated.Bytes[DelegationLength..].IndexOfAnyExcept((byte)0) >= 0 ||
                            !Eip7702Constants.IsDelegatedCode(delegated.Bytes[..DelegationLength]))
                            throw new InvalidDataException("Invalid delegation header.");
                        accountCodeHash = ValueKeccak.Compute(delegated.Bytes[..DelegationLength]);
                        batch.Next().SetCode(accountCodeHash, new CodeInfo(delegated.Bytes[..DelegationLength].ToArray()));
                    }
                    else
                    {
                        accountCodeHash = codeHash ?? throw new InvalidDataException("Account has no code-hash leaf.");
                        if (codeSize == 0)
                        {
                            if (accountCodeHash != Keccak.OfAnEmptyString.ValueHash256)
                                throw new InvalidDataException("Codeless account has a nonempty code hash.");
                        }
                        else
                        {
                            if ((ulong)codeSize + ((ulong)codeSize + 30) / 31 * 32 > (ulong)maxBufferedCodeBytes)
                                throw new PbtImageResourceLimitException("Code staging requires a larger local buffering budget.");
                            if (codeSizes.TryGetValue(accountCodeHash, out int seenSize) && seenSize != codeSize)
                                throw new InvalidDataException("Accounts claim one code hash with different code sizes.");
                            codeSizes[accountCodeHash] = codeSize;
                        }
                    }
                    batch.Next().SetAccount(addressHash, new Account(nonce, balance, Keccak.EmptyTreeHash, new Hash256(accountCodeHash)));
                    accounts++;
                    stem = null;
                    basicData = default;
                    codeHash = null;
                    delegation = null;
                }

                void FlushRun()
                {
                    if (runKey is not { } key) return;
                    batch.Next().SetSlotRun(key, run);
                    SlotRun.Return(run);
                    run = SlotRun.Empty;
                    runKey = null;
                }
            });

            using MappedByteFile table = new(chunkPath);
            long consumed = 0;
            foreach ((ValueHash256 hash, int size) in codeSizes)
            {
                byte[] code = Assemble(table, hash, size, cancellationToken);
                if (ValueKeccak.Compute(code) != hash || Eip7702Constants.IsDelegatedCode(code))
                    throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
                byte[] encodedChunks = PbtKeyDerivation.ChunkifyCode(code);
                for (int chunk = 0; chunk * 32 < encodedChunks.Length; chunk++)
                {
                    ValueHash256 expected = new(encodedChunks.AsSpan(chunk * 32, 32));
                    bool stored = TryReadChunk(table, hash, chunk, out ValueHash256 actual);
                    if (actual != expected || stored != (expected != default))
                        throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
                    if (stored) consumed++;
                }
                batch.Next().SetCode(hash, new CodeInfo(code));
            }
            if (consumed != chunkCount) throw new InvalidDataException("Snapshot holds code chunks no account's code accounts for.");
            return (accounts, slots);
        }
        finally
        {
            stagedState?.Dispose();
            File.Delete(chunkPath);
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

    /// <summary>Reassembles <paramref name="size"/> bytes of code from its stored chunks; absent chunks read as zeros.</summary>
    private static byte[] Assemble(MappedByteFile table, in ValueHash256 codeHash, int size, CancellationToken cancellationToken)
    {
        byte[] code = new byte[size];
        int chunks = (int)(((long)size + 30) / 31);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadChunk(table, codeHash, chunk, out ValueHash256 value))
                value.Bytes.Slice(1, Math.Min(31, size - chunk * 31)).CopyTo(code.AsSpan(chunk * 31));
        }
        return code;
    }

    private static bool TryReadChunk(MappedByteFile table, in ValueHash256 codeHash, int chunk, out ValueHash256 value)
    {
        PbtStorageTreeKey key = (PbtStorageTreeKey)PbtStateKey.Code(codeHash, chunk);
        if (!SortedTableReader.TrySeek<MappedByteFile, NoOpPin>(in table, new Bound(0, table.Length), key.Bytes, out Bound found))
        {
            value = default;
            return false;
        }
        Span<byte> bytes = stackalloc byte[32];
        if (!table.TryRead(found.Offset, bytes)) throw new InvalidDataException("Truncated code chunk.");
        value = new ValueHash256(bytes);
        return true;
    }
}
