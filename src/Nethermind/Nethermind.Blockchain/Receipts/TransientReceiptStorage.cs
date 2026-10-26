// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Receipts;

/// <summary>Retain recent receipts without disk writes, until finalization or the memory budget is reached.</summary>
/// <remarks>
/// The budget accounts conservatively for receipt payloads, logs and indexes. Oldest entries are evicted first
/// during prolonged non-finality; queries and reorg notifications outside the retained window may lack receipts.
/// </remarks>
public sealed class TransientReceiptStorage : IReceiptMigrationStore, IDisposable
{
    private sealed record Entry(ulong Number, TxReceipt[] Receipts, long Size, LinkedListNode<Hash256> Node)
    {
        public bool Canonical { get; set; }
    }

    private readonly IBlockTree _blockTree;
    private readonly IReceiptsRecovery _recovery;
    private readonly ILogger _logger;
    private readonly long _maxBytes;
    private readonly Lock _lock = new();
    private readonly Dictionary<Hash256, Entry> _entries = [];
    private readonly Dictionary<Hash256, Hash256> _transactions = [];
    private readonly LinkedList<Hash256> _oldestFirst = new();
    private long _bytes;
    private ulong _finalized;
    private bool _warnedCapacity;

    /// <summary>Create an in-memory store bounded by <see cref="IReceiptConfig.MaxTransientReceiptStorageSize"/>.</summary>
    public TransientReceiptStorage(IBlockTree blockTree, IReceiptsRecovery recovery, IReceiptConfig config, ILogManager logManager)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.MaxTransientReceiptStorageSize);
        _blockTree = blockTree;
        _recovery = recovery;
        _logger = logManager.GetClassLogger<TransientReceiptStorage>();
        _maxBytes = (long)config.MaxTransientReceiptStorageSize * 1024 * 1024;
        _finalized = blockTree.LastFinalizedBlockLevel;
        blockTree.BlockAddedToMain += OnCanonical;
        blockTree.BlockRemovedFromMain += OnRemoved;
        blockTree.BlocksFinalized += OnFinalized;
    }

    /// <inheritdoc/>
    public event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts;
    /// <inheritdoc/>
    public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;
    /// <inheritdoc/>
    public ulong MigratedBlockNumber { get; set; }

    /// <inheritdoc/>
    public Hash256? FindBlockHash(Hash256 txHash)
    {
        lock (_lock) return _transactions.GetValueOrDefault(txHash);
    }

    /// <inheritdoc/>
    public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
    {
        TxReceipt[] receipts = Get(block.Hash!);
        if (recover) _recovery.TryRecover(block, receipts, recoverSender);
        return receipts;
    }

    /// <inheritdoc/>
    public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
    {
        lock (_lock) return _entries.TryGetValue(blockHash, out Entry? entry) ? entry.Receipts : [];
    }

    /// <inheritdoc/>
    public bool CanGetReceiptsByHash(ulong blockNumber) => true;

    /// <inheritdoc/>
    public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
    {
        // The ordinary finder path preserves the same recovery and retention semantics.
        iterator = default;
        return false;
    }

    /// <inheritdoc/>
    public bool HasBlock(ulong blockNumber, Hash256 hash)
    {
        lock (_lock) return _entries.TryGetValue(hash, out Entry? entry) && entry.Number == blockNumber;
    }

    /// <inheritdoc/>
    public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null) =>
        Insert(block, txReceipts, null!, ensureCanonical, writeFlags, lastBlockNumber);

    /// <inheritdoc/>
    public void Insert(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null)
    {
        if (txReceipts is null) return;
        Hash256 hash = block.Hash!;
        long size = EstimateSize(txReceipts);
        lock (_lock)
        {
            bool canonical = _entries.TryGetValue(hash, out Entry? previous) && previous.Canonical;
            Remove(hash);
            if (size <= _maxBytes)
            {
                while (_bytes > _maxBytes - size)
                {
                    WarnCapacity();
                    Remove(_oldestFirst.First!.Value);
                }
                for (int i = 0; i < txReceipts.Length; i++)
                {
                    TxReceipt receipt = txReceipts[i];
                    receipt.BlockHash = hash;
                    receipt.BlockNumber = block.Number;
                    receipt.Index = i;
                    if (i < block.Transactions.Length) receipt.TxHash = block.Transactions[i].Hash;
                }
                _entries.Add(hash, new Entry(block.Number, txReceipts, size, _oldestFirst.AddLast(hash)));
                _bytes += size;
                if (ensureCanonical || canonical) Index(hash);
            }
            else
            {
                WarnCapacity();
            }
        }
        ReceiptsInserted?.Invoke(this, new ReceiptsEventArgs(block.Header, txReceipts));
    }

    /// <inheritdoc/>
    public void EnsureCanonical(Block block)
    {
        lock (_lock) Index(block.Hash!);
    }

    private void Index(Hash256 hash)
    {
        if (!_entries.TryGetValue(hash, out Entry? entry)) return;
        entry.Canonical = true;
        foreach (TxReceipt receipt in entry.Receipts)
            if (receipt.TxHash is { } txHash) _transactions[txHash] = hash;
    }

    private void Unindex(Hash256 hash)
    {
        if (!_entries.TryGetValue(hash, out Entry? entry)) return;
        entry.Canonical = false;
        foreach (TxReceipt receipt in entry.Receipts)
            if (receipt.TxHash is { } txHash && _transactions.GetValueOrDefault(txHash) == hash)
                _transactions.Remove(txHash);
    }

    private void OnCanonical(object? sender, BlockReplacementEventArgs e)
    {
        lock (_lock)
        {
            if (e.PreviousBlock is { } previous) Unindex(previous.Hash!);
            Index(e.Block.Hash!);
            // The monitor captures this store's arrays synchronously, before finalization or eviction can remove them.
            NewCanonicalReceipts?.Invoke(this, e);
            if (e.Block.Number <= _finalized) Remove(e.Block.Hash!);
        }
    }

    private void OnRemoved(object? sender, BlockHeaderEventArgs e)
    {
        lock (_lock) Unindex(e.Header.Hash!);
    }

    private void OnFinalized(object? sender, FinalizeEventArgs e)
    {
        lock (_lock)
        {
            _finalized = Math.Max(_finalized, e.FinalizedBlock.Number);
            List<Hash256> expired = [];
            foreach ((Hash256 hash, Entry entry) in _entries)
                if (entry.Number <= _finalized) expired.Add(hash);
            foreach (Hash256 hash in expired) Remove(hash);
        }
    }

    private void Remove(Hash256 hash)
    {
        if (!_entries.TryGetValue(hash, out Entry? entry)) return;
        Unindex(hash);
        _entries.Remove(hash);
        _oldestFirst.Remove(entry.Node);
        _bytes -= entry.Size;
    }

    /// <inheritdoc/>
    public void RemoveReceipts(Block block) => RemoveReceipts(block.Number, block.Hash!);

    /// <inheritdoc/>
    public void RemoveReceipts(ulong blockNumber, Hash256 blockHash)
    {
        lock (_lock) Remove(blockHash);
    }

    /// <inheritdoc/>
    public void RemoveReceiptsRange(ulong fromInclusive, ulong toExclusive)
    {
        lock (_lock)
        {
            List<Hash256> expired = [];
            foreach ((Hash256 hash, Entry entry) in _entries)
                if (entry.Number >= fromInclusive && entry.Number < toExclusive) expired.Add(hash);
            foreach (Hash256 hash in expired) Remove(hash);
        }
    }

    /// <inheritdoc/>
    public void InsertForMigration(Block block, TxReceipt[] receipts) => Insert(block, receipts);

    private void WarnCapacity()
    {
        if (_warnedCapacity) return;
        _warnedCapacity = true;
        if (_logger.IsWarn) _logger.Warn($"Transient receipt storage reached its {_maxBytes} byte budget. Oldest receipts are evicted and oversized blocks are not retained; older queries and reorg logs may be unavailable.");
    }

    internal static long EstimateSize(TxReceipt[] receipts)
    {
        long size = 512;
        foreach (TxReceipt receipt in receipts)
        {
            size += 1024L + (receipt.ReturnValue?.LongLength ?? 0) + 2L * (receipt.Error?.Length ?? 0);
            size += LogsSize(receipt.Logs);
            if (receipt.FrameReceipts is { } frames)
                foreach (TxFrameReceipt frame in frames) size += 128 + LogsSize(frame.Logs);
        }
        return size;
    }

    private static long LogsSize(LogEntry[]? logs)
    {
        long size = 32;
        if (logs is not null)
            foreach (LogEntry log in logs) size += 128L + log.Data.LongLength + 64L * log.Topics.Length;
        return size;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= OnCanonical;
        _blockTree.BlockRemovedFromMain -= OnRemoved;
        _blockTree.BlocksFinalized -= OnFinalized;
        lock (_lock)
        {
            _entries.Clear();
            _transactions.Clear();
            _oldestFirst.Clear();
            _bytes = 0;
        }
    }
}
