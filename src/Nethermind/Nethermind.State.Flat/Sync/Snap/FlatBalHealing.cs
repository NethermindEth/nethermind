// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

/// <summary>Heals the mixed flat state left by snap sync by rebuilding its trie and replaying BALs onto it.</summary>
public class FlatBalHealing(
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    TrieReassembler trieReassembler,
    IPersistence persistence,
    ITreeSyncStore store,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();

    private const int BalsChunkSize = 16;
    private const int MaxInitialCapacity = 1024;

    public bool IsAvailable => true;

    public Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token)
    {
        Hash256? reassembledRoot = trieReassembler.TryReassemble(updatedStorages, token);
        if (reassembledRoot is null)
        {
            if (_logger.IsDebug) _logger.Debug("BAL healing cannot start - trie reassembly produced no root.");
            return null;
        }

        if (_logger.IsInfo) _logger.Info($"Trie reassembly produced base state root {reassembledRoot}.");
        return reassembledRoot;
    }

    public (bool BaseRootIntact, Hash256? Root) ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Applying BALs for blocks {from.Number + 1}..{to.Number} on {baseRoot} to reach {to.StateRoot}.");

        int capacity = (int)Math.Min(to.Number.SaturatingSub(from.Number), MaxInitialCapacity);
        using ArrayPoolList<(ulong Number, Hash256 Hash)> toApply = new(capacity);

        if (!TryCollectBals(from, to, toApply, token))
            return (true, null);

        if (_logger.IsDebug) _logger.Debug($"All {toApply.Count} BALs present for blocks {from.Number + 1}..{to.Number}.");

        return ApplyBals(baseRoot, to, toApply.AsSpan(), token);
    }

    public void FinalizeSync(BlockHeader pivot) => store.FinalizeSync(pivot);

    private bool TryCollectBals(BlockHeader from, BlockHeader to, ArrayPoolList<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        for (ulong number = from.Number + 1; number <= to.Number; number++)
        {
            token.ThrowIfCancellationRequested();

            BlockHeader? header = blockTree.FindHeader(number);
            if (header?.Hash is null)
            {
                if (_logger.IsInfo) _logger.Info($"Header missing for block {number}");
                return false;
            }

            if (!balStore.Exists(number, header.Hash))
            {
                if (_logger.IsInfo) _logger.Info($"BAL missing for block {number} ({header.Hash})");
                return false;
            }

            toApply.Add((number, header.Hash));
        }

        return true;
    }

    private (bool BaseRootIntact, Hash256? Root) ApplyBals(Hash256 baseRoot, BlockHeader to, ReadOnlySpan<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        Hash256 currentRoot = baseRoot;

        int cursor = 0;
        while (cursor < toApply.Length)
        {
            token.ThrowIfCancellationRequested();

            int chunkSize = Math.Min(BalsChunkSize, toApply.Length - cursor);
            ReadOnlySpan<(ulong Number, Hash256 Hash)> chunk = toApply.Slice(cursor, chunkSize);
            Hash256? nextRoot = ApplyChunk(currentRoot, chunk, token);
            // A chunk fails before it writes anything, so until the first one commits the state is still at
            // baseRoot and the range can be applied again.
            if (nextRoot is null) return (cursor == 0, null);
            currentRoot = nextRoot;
            cursor += chunkSize;
            Metrics.BalHealingBalsApplied += chunkSize;

            float progress = (float)cursor / toApply.Length;
            if (_logger.IsInfo) _logger.Info($"BAL healing: applying BALs ({progress,8:P2}) {Progress.GetMeter(progress, 1)} block {chunk[^1].Number}");
        }

        // BALs are validated against their header's BAL hash before being stored, so a mismatch here is never
        // bad peer data: either the base state and the applied range disagree (a reorg moved the canonical
        // chain under the pivots) or the apply logic is wrong.
        if (currentRoot != to.StateRoot)
        {
            if (_logger.IsError) _logger.Error($"BAL apply of {toApply.Length} blocks up to {to.Number} produced {currentRoot}, expected {to.StateRoot}.");
            return (false, null);
        }

        if (_logger.IsDebug) _logger.Debug($"BAL apply reached target state root {currentRoot}.");
        return (false, currentRoot);
    }

    private Hash256? ApplyChunk(Hash256 baseRoot, ReadOnlySpan<(ulong Number, Hash256 Hash)> chunk, CancellationToken token)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), logManager)
        {
            RootHash = baseRoot
        };

        Dictionary<AddressAsKey, AccountDelta> deltas = [];
        foreach ((ulong number, Hash256 hash) in chunk)
        {
            token.ThrowIfCancellationRequested();

            ReadOnlyBlockAccessList? bal = balStore.Get(number, hash);

            if (bal is null)
            {
                if (_logger.IsWarn) _logger.Warn($"BAL for block {number} ({hash}) disappeared after being collected for healing.");
                return null;
            }

            foreach (ReadOnlyAccountChanges acc in bal.AccountChanges)
            {
                if (!acc.HasStateChanges) continue;

                ref AccountDelta? delta = ref CollectionsMarshal.GetValueRefOrAddDefault(deltas, acc.Address, out _);
                delta ??= new AccountDelta();

                if (acc.BalanceChanges.Length > 0) delta.Balance = acc.BalanceChanges[^1].Value;
                if (acc.NonceChanges.Length > 0) delta.Nonce = acc.NonceChanges[^1].Value;
                if (acc.CodeChanges.Length > 0) delta.Code = acc.CodeChanges[^1];

                if (acc.StorageChanges.Length > 0)
                {
                    Dictionary<UInt256, EvmWord> slots = delta.Slots ??= [];
                    foreach (ReadOnlySlotChanges slot in acc.StorageChanges)
                        if (slot.Changes.Length > 0) slots[slot.Key] = slot.Changes[^1].Value;
                }
            }
        }

        Span<byte> slotValue = stackalloc byte[EvmWord.Count];
        foreach ((AddressAsKey key, AccountDelta delta) in deltas)
        {
            token.ThrowIfCancellationRequested();

            Address address = key.Value;

            Account account = reader.GetAccount(address) ?? Account.TotallyEmpty;

            if (delta.Balance is { } balance) account = account.WithChangedBalance(balance);
            if (delta.Nonce is { } nonce) account = account.WithChangedNonce(nonce);
            if (delta.Code is { } codeChange)
            {
                ValueHash256 codeHash = codeChange.CodeHash;
                codeDb.Set(codeHash.Bytes, codeChange.Code);
                account = account.WithChangedCodeHash(codeHash.ToCommitment());
            }

            // EIP-158: a touched empty account is removed, whatever its storage. Wipe it before writing any
            // slot - SelfDestruct only scans the pre-batch snapshot, so slots written into this batch would
            // outlive it. A revive later in the chunk skips the wipe, which EIP-6780 makes safe.
            if (account.IsEmpty)
            {
                batch.SelfDestruct(address);
                stateTree.Set(address, null);
                batch.SetAccount(address, null);
                continue;
            }

            if (delta.Slots is { Count: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    account.StorageRoot,
                    logManager);

                foreach ((UInt256 slot, EvmWord word) in slots)
                {
                    word.CopyTo(slotValue);
                    ReadOnlySpan<byte> trimmed = slotValue.WithoutLeadingZeros();
                    storage.Set(slot, trimmed.ToArray());
                    batch.SetStorage(address, slot, trimmed.IsZero() ? null : SlotValue.FromSpanWithoutLeadingZero(trimmed));
                }

                storage.Commit(false, WriteFlags.DisableWAL);
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            stateTree.Set(address, account);
            batch.SetAccount(address, account);
        }

        stateTree.Commit(false, WriteFlags.DisableWAL);
        return stateTree.RootHash;
    }

    private sealed class AccountDelta
    {
        public UInt256? Balance;
        public ulong? Nonce;
        public CodeChange? Code;
        public Dictionary<UInt256, EvmWord>? Slots;
    }
}
