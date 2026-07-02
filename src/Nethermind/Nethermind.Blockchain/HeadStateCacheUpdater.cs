// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain;

/// <summary>
/// Keeps the <see cref="HeadStateCache"/> coherent with the canonical head. On a sequential head
/// advance it refreshes the keys the block changed; on a reorg, gap, or self-destruct it flushes
/// (safe: reads then rebuild lazily via backfill).
/// </summary>
/// <remarks>
/// <para>
/// Updates are applied on a single background worker, not on the <see cref="IBlockTree.BlockAddedToMain"/>
/// handler thread, so the (potentially trie-bound) refresh never delays block processing or other event
/// subscribers. Events are enqueued in order; if the worker falls behind the bounded queue drops events,
/// which is safe — a dropped event makes the next one non-sequential, triggering a flush + lazy rebuild.
/// </para>
/// <para>
/// Changed accounts and storage come, in priority order, from: the per-block journal capture
/// (<see cref="HeadStateDeltaBuffer"/>, works on any node), the consensus Block Access List, or the BAL
/// generated during processing. The pooled <see cref="Block.AccountChanges"/> is deliberately not read
/// (the TxPool owns and disposes it concurrently).
/// </para>
/// </remarks>
public sealed class HeadStateCacheUpdater : IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly HeadStateCache _cache;
    private readonly IStateReader _stateReader;
    private readonly HeadStateDeltaBuffer? _deltaBuffer;
    private readonly ILogger _logger;
    private readonly Channel<(Block Block, bool HadPreviousBlock)> _queue =
        Channel.CreateBounded<(Block, bool)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public HeadStateCacheUpdater(IBlockTree blockTree, HeadStateCache cache, IStateReader stateReader, ILogManager logManager, HeadStateDeltaBuffer? deltaBuffer = null)
    {
        _blockTree = blockTree;
        _cache = cache;
        _stateReader = stateReader;
        _deltaBuffer = deltaBuffer;
        _logger = logManager.GetClassLogger<HeadStateCacheUpdater>();
        _worker = Task.Run(ProcessLoopAsync);
        _blockTree.BlockAddedToMain += OnBlockAddedToMain;
    }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Hash is null) return;
        // Enqueue for off-thread processing; never block the event source. A dropped event (queue full)
        // self-heals: the next event is non-sequential and forces a flush.
        _queue.Writer.TryWrite((e.Block, e.PreviousBlock is not null));
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach ((Block block, bool hadPreviousBlock) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                Apply(block, hadPreviousBlock);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void Apply(Block block, bool hadPreviousBlock)
    {
        if (block.Hash is null) return;
        try
        {
            bool sequential = !hadPreviousBlock
                && _cache.HeadHash is not null
                && _cache.HeadHash.Equals(block.ParentHash);

            if (!sequential || !TryCollectChangedKeys(block, out FrozenSet<AddressAsKey> changedAccounts, out FrozenSet<StorageCell> changedSlots))
            {
                _cache.Flush(block.Hash);
                return;
            }

            _cache.Advance(block.Hash, changedAccounts, changedSlots, new Refresher(_stateReader, block.Header));
        }
        catch (Exception ex)
        {
            // Never serve stale state: drop everything and re-anchor at the new head.
            if (_logger.IsWarn) _logger.Warn($"Head state cache update failed for {block.ToString(Block.Format.Short)}, flushing. {ex}");
            _cache.Flush(block.Hash);
        }
    }

    private bool TryCollectChangedKeys(
        Block block,
        out FrozenSet<AddressAsKey> changedAccounts,
        out FrozenSet<StorageCell> changedSlots)
    {
        changedAccounts = FrozenSet<AddressAsKey>.Empty;
        changedSlots = FrozenSet<StorageCell>.Empty;

        // 1) Journal capture: works on any node, regardless of EIP-7928 / Block Access Lists. The delta
        // carries both changed accounts and slots captured from processing — we do NOT read the pooled
        // Block.AccountChanges (the TxPool owns and disposes it concurrently).
        if (block.Header.StateRoot is { } stateRoot
            && _deltaBuffer is not null
            && _deltaBuffer.TryGet((ulong)block.Number, stateRoot, out HeadStateBlockDelta delta))
        {
            if (delta.RequiresFlush) return false; // self-destruct: can't enumerate cleared slots
            changedAccounts = delta.ChangedAccounts;
            changedSlots = delta.ChangedSlots;
            return true;
        }

        // 2) Consensus or processing-generated Block Access List.
        HashSet<AddressAsKey> accounts = [];
        HashSet<StorageCell> slots = [];
        if (block.BlockAccessList is { } bal)
        {
            foreach (ReadOnlyAccountChanges ac in bal.AccountChanges)
            {
                // Any change (including storage, which moves the storage root) makes the account stale.
                if (ac.HasStateChanges) accounts.Add(ac.Address);
                foreach (UInt256 slot in ac.ChangedSlots) slots.Add(new StorageCell(ac.Address, slot));
            }
        }
        else if (block.GeneratedBlockAccessList is { } generated)
        {
            foreach (GeneratedAccountChanges ac in generated.AccountChanges)
            {
                bool storageChanged = ac.StorageChanges.Count > 0;
                if (storageChanged || ac.BalanceChanges.Count > 0 || ac.NonceChanges.Count > 0 || ac.CodeChanges.Count > 0)
                {
                    accounts.Add(ac.Address);
                }
                foreach (GeneratedSlotChanges slot in ac.StorageChanges) slots.Add(new StorageCell(ac.Address, slot.Key));
            }
        }
        else
        {
            return false;
        }

        changedAccounts = accounts.ToFrozenSet();
        changedSlots = slots.ToFrozenSet();
        return true;
    }

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { /* best-effort shutdown */ }
        _cts.Dispose();
    }

    private sealed class Refresher(IStateReader stateReader, BlockHeader header) : IHeadStateRefresher
    {
        public Account? GetAccount(Address address) =>
            stateReader.TryGetAccount(header, address, out AccountStruct account)
                ? new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment())
                : null;

        public byte[] GetStorage(in StorageCell cell) =>
            stateReader.GetStorage(header, cell.Address, cell.Index).ToArray();
    }
}
