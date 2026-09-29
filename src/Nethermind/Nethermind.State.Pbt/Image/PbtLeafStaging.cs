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

namespace Nethermind.State.Pbt.Image;

/// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream.</summary>
/// <remarks>
/// Leaves carry no storage roots, so accounts are staged with the empty tree root; PBT never treats a storage root as
/// authoritative. Code chunks are keyed by code hash rather than by account, so they are tabled as they stream past
/// and each bytecode is reassembled once the whole stream, and with it every code size, has been seen.
/// </remarks>
internal static class PbtLeafStaging
{
    private const byte AccountZone = 0x00;
    private const byte CodeZone = 0x01;
    private const int DelegationLength = 23;

    public static (ulong Accounts, ulong Slots) Stage(PbtAnchorPublication.LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
        string scratchDirectory, CancellationToken cancellationToken)
    {
        string chunkPath = Path.Combine(scratchDirectory, $"pbt-code-{Guid.NewGuid():N}");
        Dictionary<ValueHash256, int> codeSizes = [];
        ulong accounts = 0;
        ulong slots = 0;
        try
        {
            PbtImageVerifier.BuildTable(chunkPath, (ref SortedTableBuilder<ArenaBufferWriter> chunks) =>
            {
                ValueHash256? stem = null;
                ValueHash256 basicData = default;
                ValueHash256? codeHash = null;
                ValueHash256? delegation = null;
                PbtStorageTreeKey? runKey = null;
                ISlotRun run = SlotRun.Empty;

                foreach (RebuildEntry entry in leaves)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> key = entry.Key.Bytes;
                    if (key[0] == CodeZone)
                    {
                        FlushAccount();
                        chunks.Add(key, entry.Leaf.Bytes);
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
                        }
                    }
                    else FlushAccount();

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
                    int codeSize = (int)PbtKeyDerivation.ReadBasicDataCodeSize(basicData.Bytes);
                    PbtKeyDerivation.UnpackBasicData(basicData.Bytes, out ulong nonce, out UInt256 balance);
                    ValueHash256 accountCodeHash = delegation is { } delegated
                        ? ValueKeccak.Compute(delegated.Bytes[..DelegationLength])
                        : codeHash ?? Keccak.OfAnEmptyString.ValueHash256;
                    batch.Next().SetAccount(addressHash, new Account(nonce, balance, Keccak.EmptyTreeHash, new Hash256(accountCodeHash)));
                    if (delegation is { } delegatedCode)
                        batch.Next().SetCode(accountCodeHash, new CodeInfo(delegatedCode.Bytes[..DelegationLength].ToArray()));
                    else if (codeSize != 0)
                        codeSizes.TryAdd(accountCodeHash, codeSize);
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
            PbtImageVerifier.CodeTable chunkTable = new(table);
            foreach ((ValueHash256 hash, int size) in codeSizes)
                batch.Next().SetCode(hash, new CodeInfo(chunkTable.Assemble(hash, size, cancellationToken)));
            return (accounts, slots);
        }
        finally { File.Delete(chunkPath); }
    }
}
