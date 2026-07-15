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
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
// using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

public class FlatBalHealing(
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    ITrieReassembler trieReassembler,
    // ITreeSyncStore store,
    IPersistence persistence,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager,
    IVerifyTrieStarter? verifyTrieStarter = null) : IBalHealing
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatBalHealing>();

    public Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Starting FlatBalHealing from block {firstPivot.Number} to {lastPivot.Number}.");

        int capacity = (int)Math.Min(lastPivot.Number.SaturatingSub(firstPivot.Number), MaxInitialCapacity);
        ArrayPoolListRef<(ulong Number, Hash256 Hash)> toApply = new(capacity);
        try
        {
            if (!TryCollectBals(firstPivot, lastPivot, ref toApply, token))
                return Task.FromResult(false);

            if (_logger.IsInfo) _logger.Info($"All {toApply.Count} BALs present for the pivot range.");

            // VerifyStorageCompleteness(updatedStorageAccounts, token);

            Hash256? reassembledRoot = trieReassembler.TryReassemble(updatedStorageAccounts);
            StateId from;
                using (IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync))
                    from = reader.CurrentState;

            persistence.Flush(); // DisableWAL writes are only durable after flush; flush before moving the pointer
            using (persistence.CreateWriteBatch(from, new StateId(firstPivot.Number, reassembledRoot), WriteFlags.DisableWAL)) { }
                
            verifyTrieStarter?.TryStartVerifyTrie(firstPivot);

            if (reassembledRoot is null)
            {
                if (_logger.IsInfo) _logger.Info("Trie reassembly produced no root — falling back to traditional state sync.");
                return Task.FromResult(false);
            }

            if (_logger.IsInfo) _logger.Info($"Trie reassembly produced state root {reassembledRoot}. Applying BALs to reach {lastPivot.StateRoot}.");

            // VerifyReassembledStructure(reassembledRoot, token);

            if (!ApplyBals(reassembledRoot, lastPivot, toApply.AsSpan(), token))
            {
                if (_logger.IsInfo) _logger.Info($"Applying BALs failed to reach {lastPivot.StateRoot} — falling back to traditional state sync.");
                return Task.FromResult(false);
            }

            // store.FinalizeSync(lastPivot);

            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(false);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("BAL healing failed — falling back to traditional state sync.", e);
            return Task.FromResult(false);
        }
        finally
        {
            toApply.Dispose();
        }
    }

    /// <summary>
    /// Diagnostic: scans every persisted account and reports those whose storage trie is incomplete
    /// (storage-root node absent) yet are NOT in the heal set — i.e. accounts the reassembler would
    /// reuse with a dangling <c>StorageRoot</c>. Under the invariant
    /// <c>persisted ∧ not-heal-flagged ⟹ complete</c> this count must be zero; any non-zero count is
    /// the root cause of a wrong reassembled root. Each dangling account is classified by whether its
    /// flat storage leaves are present (reassemble-able) or missing (must be re-downloaded).
    /// </summary>
    private void VerifyStorageCompleteness(IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token)
    {
        HashSet<ValueHash256> flagged = new(updatedStorageAccounts.Count);
        foreach (Hash256 h in updatedStorageAccounts) flagged.Add(h.ValueHash256);

        AccountDecoder decoder = AccountDecoder.Slim;
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);

        long scanned = 0, withStorage = 0, danglingFlagged = 0, danglingUnflagged = 0, danglingUnflaggedNoLeaves = 0;

        using IPersistence.IFlatIterator it = reader.CreateAccountIterator();
        while (it.MoveNext())
        {
            if (token.IsCancellationRequested) return;
            scanned++;

            Account? account = decoder.Decode(it.CurrentValue);
            if (account is null || account.StorageRoot == Keccak.EmptyTreeHash) continue;
            withStorage++;

            Hash256 owner = it.CurrentKey.ToCommitment();
            if (reader.TryLoadStorageRlp(owner, TreePath.Empty, ReadFlags.None) is not null) continue;

            // Storage-root node absent → storage trie incomplete on disk.
            if (flagged.Contains(it.CurrentKey))
            {
                danglingFlagged++;
                continue;
            }

            danglingUnflagged++;
            bool hasFlatLeaves;
            using (IPersistence.IFlatIterator sit = reader.CreateStorageIterator(owner))
                hasFlatLeaves = sit.MoveNext();
            if (!hasFlatLeaves) danglingUnflaggedNoLeaves++;

            if (_logger.IsWarn && danglingUnflagged <= 32)
                _logger.Warn($"Dangling storage UNFLAGGED: account {it.CurrentKey} storageRoot {account.StorageRoot} flatLeaves={hasFlatLeaves}");
        }

        if (_logger.IsInfo)
            _logger.Info($"Storage completeness: scanned {scanned}, withStorage {withStorage}, dangling(flagged) {danglingFlagged}, dangling(UNFLAGGED) {danglingUnflagged} (of which no flat leaves {danglingUnflaggedNoLeaves}).");
    }

    /// <summary>
    /// Diagnostic: walks the reassembled trie at <paramref name="reassembledRoot"/> and asserts it structurally
    /// contains exactly the flat account set — every flat account is reachable, and each account's storage trie is
    /// walkable from its (rewritten) storage root. Value mismatches are ignored (the reassembled state is a pre-BAL
    /// mix); only structural drops (dropped accounts, dangling storage) are reported. A correct reassembly reports
    /// zero of both. This bypasses <see cref="FlatTrieVerifier"/>'s CurrentState fallback by walking the reassembled
    /// root explicitly.
    /// </summary>
    private void VerifyReassembledStructure(Hash256 reassembledRoot, CancellationToken token)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), logManager);
        AccountDecoder decoder = AccountDecoder.Slim;

        long scanned = 0, missingInTrie = 0, withStorage = 0, danglingStorage = 0;

        using IPersistence.IFlatIterator it = reader.CreateAccountIterator();
        while (it.MoveNext())
        {
            if (token.IsCancellationRequested) return;
            scanned++;

            ReadOnlySpan<byte> path = reader.IsPreimageMode
                ? Keccak.Compute(it.CurrentKey.Bytes[..20]).Bytes
                : it.CurrentKey.Bytes;

            ReadOnlySpan<byte> trieRlp = stateTree.Get(path, reassembledRoot);
            if (trieRlp.IsEmpty)
            {
                missingInTrie++;
                if (_logger.IsWarn && missingInTrie <= 32)
                    _logger.Warn($"Reassembled trie MISSING flat account: {it.CurrentKey}");
                continue;
            }

            Account? account = decoder.Decode(it.CurrentValue);
            if (account is null || account.StorageRoot == Keccak.EmptyTreeHash) continue;
            withStorage++;

            Hash256 owner = it.CurrentKey.ToCommitment();
            if (reader.TryLoadStorageRlp(owner, TreePath.Empty, ReadFlags.None) is null)
            {
                danglingStorage++;
                if (_logger.IsWarn && danglingStorage <= 32)
                    _logger.Warn($"Reassembled trie DANGLING storage: account {it.CurrentKey} storageRoot {account.StorageRoot}");
            }
        }

        if (_logger.IsInfo)
            _logger.Info($"Reassembled structure ({reassembledRoot}): scanned {scanned}, missingInTrie {missingInTrie}, withStorage {withStorage}, danglingStorage {danglingStorage}.");
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
    private const int MaxInitialCapacity = 1024;

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
                    // A cleared slot must be removed from flat, not written as a zero entry: a null
                    // SlotValue hits the delete path, matching the trie which drops the leaf.
                    // WithoutLeadingZeros keeps a single 0 byte for a zero value, so test IsZero, not length.
                    batch.SetStorage(address, slot, value.IsZero() ? null : SlotValue.FromSpanWithoutLeadingZero(value));
                }

                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            // EIP-158 state clearing removes empty accounts (balance/nonce/code) regardless of storage;
            // IsTotallyEmpty would keep an emptied account alive while it still carries a storage root.
            Account? toWrite = account.IsEmpty ? null : account;
            stateTree.Set(address, toWrite);
            batch.SetAccount(address, toWrite);

            // Removing the account from the trie drops its storage subtree, but flat leaves are not
            // reachable from the trie — wipe them explicitly so stale slots don't survive in flat.
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
        public Dictionary<UInt256, byte[]>? Slots;
    }
}
