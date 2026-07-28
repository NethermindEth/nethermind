// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Flat.Sync.Snap;

public class FlatBalHealing(
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    ITrieReassembler trieReassembler,
    IPersistence persistence,
    ITreeSyncStore store,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();

    private const int BalsChunkSize = 64;
    private const int MaxInitialCapacity = 1024;

    public Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages)
    {
        Hash256? reassembledRoot = trieReassembler.TryReassemble(updatedStorages);
        if (reassembledRoot is null)
        {
            if (_logger.IsWarn) _logger.Warn("Trie reassembly produced no root");
            return null;
        }

        if (_logger.IsInfo) _logger.Info($"Trie reassembly produced base state root {reassembledRoot}.");
        return reassembledRoot;
    }

    public Hash256? ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Applying BALs for blocks {from.Number + 1}..{to.Number} on {baseRoot} to reach {to.StateRoot}.");

        int capacity = (int)Math.Min(to.Number.SaturatingSub(from.Number), MaxInitialCapacity);
        ArrayPoolListRef<(ulong Number, Hash256 Hash)> toApply = new(capacity);
        try
        {
            if (!TryCollectBals(from, to, ref toApply, token))
                return null;

            if (_logger.IsInfo) _logger.Info($"All {toApply.Count} BALs present for blocks {from.Number + 1}..{to.Number}.");

            return ApplyBals(baseRoot, to, toApply.AsSpan(), token);
        }
        finally
        {
            toApply.Dispose();
        }
    }

    public void FinalizeSync(BlockHeader pivot) => store.FinalizeSync(pivot);

    private bool TryCollectBals(BlockHeader from, BlockHeader to, ref ArrayPoolListRef<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        ulong blockNumber = from.Number + 1;
        while (blockNumber <= to.Number)
        {
            token.ThrowIfCancellationRequested();

            BlockHeader? header = blockTree.FindHeader(blockNumber);
            if (header is null)
                return false;

            if (!balStore.Exists(header.Number, header.Hash!))
            {
                // TODO; get bals from peers explicitly. curently works cause forward sync will download them but not guarantee
                if (_logger.IsInfo) _logger.Info($"BAL missing for block {header.Number} ({header.Hash})");
                return false;
            }

            toApply.Add((header.Number, header.Hash!));

            blockNumber++;
        }

        return true;
    }

    private Hash256? ApplyBals(Hash256 baseRoot, BlockHeader to, ReadOnlySpan<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        Hash256 currentRoot = baseRoot;

        int cursor = 0;
        while (cursor < toApply.Length)
        {
            token.ThrowIfCancellationRequested();

            int chunkSize = Math.Min(BalsChunkSize, toApply.Length - cursor);
            ReadOnlySpan<(ulong Number, Hash256 Hash)> chunk = toApply.Slice(cursor, chunkSize);
            Hash256? nextRoot = ApplyChunk(currentRoot, chunk, token);
            if (nextRoot is null) return null;
            currentRoot = nextRoot;
            cursor += chunkSize;

            float progress = (float)cursor / toApply.Length;
            if (_logger.IsInfo) _logger.Info($"BAL healing: applying BALs ({progress,8:P2}) {Progress.GetMeter(progress, 1)} block {chunk[^1].Number}");
        }

        if (currentRoot != to.StateRoot)
        {
            if (_logger.IsInfo) _logger.Info($"BAL apply produced {currentRoot}, expected {to.StateRoot}");
            return null;
        }

        if (_logger.IsInfo) _logger.Info($"BAL apply reached target state root {currentRoot}.");
        return currentRoot;
    }

    private Hash256? ApplyChunk(Hash256 baseRoot, ReadOnlySpan<(ulong Number, Hash256 Hash)> chunk, CancellationToken token)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), logManager)
        {
            RootHash = baseRoot
        };

        Dictionary<Address, AccountDelta> deltas = [];
        foreach ((ulong number, Hash256 hash) in chunk)
        {
            token.ThrowIfCancellationRequested();

            ReadOnlyBlockAccessList? bal = balStore.Get(number, hash);

            if (bal is null)
            {
                if (_logger.IsInfo) _logger.Info($"BAL missing for block {number} ({hash})");
                return null;
            }

            foreach (ReadOnlyAccountChanges acc in bal.AccountChanges)
            {
                if (!acc.HasStateChanges) continue;

                ref AccountDelta? delta = ref CollectionsMarshal.GetValueRefOrAddDefault(deltas, acc.Address, out _);
                delta ??= new AccountDelta();

                if (acc.BalanceChanges.Length > 0) delta.Balance = acc.BalanceChanges[^1].Value;
                if (acc.NonceChanges.Length > 0) delta.Nonce = acc.NonceChanges[^1].Value;
                if (acc.CodeChanges.Length > 0) delta.Code = acc.CodeChanges[^1].Code;

                if (acc.StorageChanges.Length > 0)
                {
                    Dictionary<UInt256, EvmWord> slots = delta.Slots ??= [];
                    foreach (ReadOnlySlotChanges slot in acc.StorageChanges)
                        slots[slot.Key] = slot.Changes[^1].Value;
                }
            }
        }

        foreach ((Address address, AccountDelta delta) in deltas)
        {
            token.ThrowIfCancellationRequested();

            Account account = reader.GetAccount(address) ?? Account.TotallyEmpty;

            if (delta.Balance is { } balance) account = account.WithChangedBalance(balance);
            if (delta.Nonce is { } nonce) account = account.WithChangedNonce(nonce);
            if (delta.Code is { } code)
            {
                Hash256 codeHash = Keccak.Compute(code);
                codeDb.Set(codeHash.Bytes, code);
                account = account.WithChangedCodeHash(codeHash);
            }

            if (delta.Slots is { Count: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    account.StorageRoot,
                    logManager);

                foreach ((UInt256 slot, EvmWord word) in slots)
                {
                    EvmWord w = word;
                    ReadOnlySpan<byte> full = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref w), 32);
                    ReadOnlySpan<byte> trimmed = full.WithoutLeadingZeros();
                    storage.Set(slot, trimmed.ToArray());
                    batch.SetStorage(address, slot, trimmed.IsEmpty ? null : SlotValue.FromSpanWithoutLeadingZero(trimmed));
                }

                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            Account? toWrite = account.IsEmpty ? null : account;
            stateTree.Set(address, toWrite);
            batch.SetAccount(address, toWrite);

            if (toWrite is null) batch.SelfDestruct(address);
        }

        stateTree.Commit();
        return stateTree.RootHash;
    }

    private sealed class AccountDelta
    {
        public UInt256? Balance;
        public ulong? Nonce;
        public byte[]? Code;
        public Dictionary<UInt256, EvmWord>? Slots;
    }
}
