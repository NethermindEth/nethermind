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
    ITreeSyncStore store,
    IPersistence persistence,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();

    public Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Starting FlatBalHealing from block {firstPivot.Number} to {lastPivot.Number}.");

        int capacity = (int)Math.Min(lastPivot.Number.SaturatingSub(firstPivot.Number), int.MaxValue);
        ArrayPoolListRef<(ulong Number, Hash256 Hash)> toApply = new(capacity);
        try
        {
            if (!TryCollectBals(firstPivot, lastPivot, ref toApply, token))
                return Task.FromResult(false);

            if (_logger.IsInfo) _logger.Info($"All {toApply.Count} BALs present for the pivot range.");

            Hash256? reassembledRoot = trieReassembler.TryReassemble(updatedStorageAccounts);
            if (reassembledRoot is null)
            {
                if (_logger.IsInfo) _logger.Info("Trie reassembly produced no root — falling back to traditional state sync.");
                return Task.FromResult(false);
            }

            if (_logger.IsInfo) _logger.Info($"Trie reassembly produced state root {reassembledRoot}. Applying BALs to reach {lastPivot.StateRoot}.");

            if (!ApplyBals(reassembledRoot, lastPivot, toApply.AsSpan(), token))
            {
                if (_logger.IsInfo) _logger.Info($"Applying BALs failed to reach {lastPivot.StateRoot} — falling back to traditional state sync.");
                return Task.FromResult(false);
            }

            store.FinalizeSync(lastPivot);

            return Task.FromResult(true);
        }
        finally
        {
            toApply.Dispose();
        }
    }

    private bool TryCollectBals(BlockHeader firstPivot, BlockHeader lastPivot, ref ArrayPoolListRef<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        ulong blockNumber = firstPivot.Number + 1;
        while (blockNumber <= lastPivot.Number)
        {
            if (token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info("FlatBalHealing cancelled.");
                return false;
            }

            BlockHeader? header = blockTree.FindHeader(blockNumber);
            if (header is null)
                return false;

            if (!balStore.Exists(header.Number, header.Hash!))
            {
                if (_logger.IsInfo) _logger.Info($"BAL missing for block {header.Number} ({header.Hash})");
                return false;
            }

            toApply.Add((header.Number, header.Hash!));

            blockNumber++;
        }

        return true;
    }
    private const int BalsChunkSize = 256;

    private bool ApplyBals(Hash256 reassembledRoot, BlockHeader lastPivot, ReadOnlySpan<(ulong Number, Hash256 Hash)> toApply, CancellationToken token)
    {
        Hash256 currentRoot = reassembledRoot;

        int cursor = 0;
        while (cursor < toApply.Length)
        {
            if (token.IsCancellationRequested) return false;

            int chunkSize = Math.Min(BalsChunkSize, toApply.Length - cursor);
            Hash256? nextRoot = ApplyChunk(currentRoot, toApply.Slice(cursor, chunkSize), token);
            if (nextRoot is null) return false;
            currentRoot = nextRoot;
            cursor += chunkSize;
        }

        if (currentRoot != lastPivot.StateRoot)
        {
            if (_logger.IsInfo) _logger.Info($"BAL apply produced {currentRoot}, expected {lastPivot.StateRoot}");
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"BAL healing reached target state root {currentRoot}.");
        return true;
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
            if (token.IsCancellationRequested) return null;

            ReadOnlyBlockAccessList? bal = balStore.Get(number, hash);

            if (bal is null)
            {
                if (_logger.IsInfo) _logger.Info($"BAL missing for block {number} ({hash})");
                return null;
            }

            foreach (ReadOnlyAccountChanges acc in bal.AccountChanges)
            {
                ref AccountDelta? delta = ref CollectionsMarshal.GetValueRefOrAddDefault(deltas, acc.Address, out _);
                delta ??= new AccountDelta();

                if (acc.BalanceChanges.Length > 0) delta.Balance = acc.BalanceChanges[^1].Value;
                if (acc.NonceChanges.Length > 0) delta.Nonce = acc.NonceChanges[^1].Value;
                if (acc.CodeChanges.Length > 0) delta.Code = acc.CodeChanges[^1].Code;

                if (acc.StorageChanges.Length > 0)
                {
                    Dictionary<UInt256, byte[]> slots = delta.Slots ??= [];
                    foreach (ReadOnlySlotChanges slot in acc.StorageChanges)
                    {
                        EvmWord word = slot.Changes[^1].Value;
                        ReadOnlySpan<byte> full = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref word), 32);
                        slots[slot.Key] = full.WithoutLeadingZeros().ToArray();
                    }
                }
            }
        }

        foreach ((Address address, AccountDelta delta) in deltas)
        {
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

                foreach ((UInt256 slot, byte[] value) in slots)
                {
                    storage.Set(slot, value);
                    batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
                }

                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            Account? toWrite = account.IsTotallyEmpty ? null : account;
            stateTree.Set(address, toWrite);
            batch.SetAccount(address, toWrite);
        }

        stateTree.Commit();
        return stateTree.RootHash;
    }

    private sealed class AccountDelta
    {
        public UInt256? Balance;
        public ulong? Nonce;
        public byte[]? Code;
        public Dictionary<UInt256, byte[]>? Slots;
    }
}
