// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt;

/// <summary>One state entry to fold into the rebuilt tree: either a full account (with its bytecode) or a single storage slot.</summary>
/// <remarks>Emit an account before its slots; header-region slots (index &lt; 64) and the account's header
/// leaves share a stem, and the rebuilder merges them regardless of arrival order.</remarks>
public readonly record struct RebuildEntry
{
    private RebuildEntry(bool isAccount, Address address, Account? account, byte[]? code, UInt256 slot, byte[]? value)
    {
        IsAccount = isAccount;
        Address = address;
        Account = account;
        Code = code;
        Slot = slot;
        Value = value;
    }

    public bool IsAccount { get; }
    public Address Address { get; }
    public Account? Account { get; }

    /// <summary>The account's bytecode, or <c>null</c> for an EOA; ignored for slot entries.</summary>
    public byte[]? Code { get; }
    public UInt256 Slot { get; }

    /// <summary>The slot's stripped (leading-zeros-removed) value; ignored for account entries.</summary>
    public byte[]? Value { get; }

    public static RebuildEntry ForAccount(Address address, Account account, byte[]? code) => new(true, address, account, code, default, null);
    public static RebuildEntry ForSlot(Address address, in UInt256 slot, byte[] value) => new(false, address, null, null, slot, value);
}

/// <summary>
/// Bulk-builds a PBT state from a stream of decoded <see cref="RebuildEntry"/> account/slot records
/// (e.g. from an iterated preimage-flat database), computing the EIP-8297 root without processing any
/// blocks. It is the single consumer of the stream; the caller (producer) scans and decodes the source.
/// </summary>
/// <remarks>
/// The rebuild runs in bounded windows to cap memory: entries accumulate into a per-stem leaf map and
/// the target's flat account/slot columns, and every <see cref="FlushEntryInterval"/> entries the window
/// is folded into the tree via <see cref="TrieUpdater.UpdateRoot"/> and committed. Data windows are
/// written <see cref="StateId.PreGenesis"/> → <see cref="StateId.PreGenesis"/> with
/// <see cref="WriteFlags.DisableWAL"/> — the persisted-state pointer stays pre-genesis so a crash
/// mid-rebuild leaves the state unpopulated and the next run restarts cleanly, which is also what
/// makes skipping the WAL safe; only a final empty batch, after a flush, atomically advances the
/// pointer to the rebuilt state.
/// Because each window folds against the previously committed windows, a stem split across windows is
/// merged correctly (the updater reads its prior leaf blob and folds the new leaves in).
/// </remarks>
public sealed class PbtRebuilder(IPbtPersistence target, ILogManager logManager, IPbtConfig config)
{
    /// <summary>Entries (accounts + slots) buffered before a window is folded into the tree and committed.</summary>
    internal int FlushEntryInterval { get; init; } = 1_000_000;

    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    /// <summary>Rebuilds the tree from <paramref name="source"/> and returns the EIP-8297 root at <paramref name="blockNumber"/>.</summary>
    /// <remarks>
    /// Reading and folding run as a pipeline: this consumer accumulates each window and hands the full
    /// one to a single flush worker over a bounded (capacity-1) channel, so the next window fills while
    /// the worker folds and commits the current one. The fold stays sequential — each window folds on
    /// the previous root — so exactly one worker drains the channel in order, and it alone touches the
    /// target database.
    /// </remarks>
    public async Task<ValueHash256> Rebuild(ChannelReader<RebuildEntry> source, ulong blockNumber, CancellationToken cancellationToken)
    {
        using CancellationTokenSource pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // capacity 1 double-buffers: one window commits while the next fills
        Channel<FlushBatch> flushChannel = Channel.CreateBounded<FlushBatch>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        ValueHash256 root = default; // empty tree is 32 zero bytes
        long stems = 0;

        // The flush worker folds each window on the previous root and commits it, in order, logging
        // progress off the just-committed window so throughput tracks durable work rather than reads
        // racing ahead. Rates are measured over each inter-log interval.
        async Task FlushLoop()
        {
            long accounts = 0, slots = 0;
            Address lastAddress = Address.Zero;
            double accountsPerSec = 0, slotsPerSec = 0, stemsPerSec = 0;
            long loggedAccounts = 0, loggedSlots = 0, loggedStems = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();

            // Accounts stream in ascending address order, so the current address's position in the key
            // space is the completion fraction (addresses are ~uniform, so the top 64 bits estimate it
            // well) — the trick FlatTrieVerifier uses off the trie path. CurrentValue is driven by the
            // entry count instead, only so it moves every window: a huge-storage account holds the
            // address (and percentage) fixed for many windows, and the logger drops a repeated line
            // unless CurrentValue changes — which would stall the log through the slowest part.
            ProgressLogger progress = new("PBT rebuild", logManager);
            progress.SetFormat(_ =>
            {
                float percentage = Math.Clamp(BinaryPrimitives.ReadUInt64BigEndian(lastAddress.Bytes) / (float)ulong.MaxValue, 0, 1);
                return $"PBT rebuild {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
                    $"{accounts,13:N0} acc ({accountsPerSec,8:N0}/s) | {slots,15:N0} slot ({slotsPerSec,8:N0}/s) | " +
                    $"{stems,13:N0} stem ({stemsPerSec,8:N0}/s) | at {lastAddress}";
            });
            progress.Reset(0, 0);

            await foreach (FlushBatch batch in flushChannel.Reader.ReadAllAsync(pipelineCts.Token))
            {
                root = FlushAndCommit(batch.WriteBatch, root, batch.Window, out PbtSubtreeStats stemDelta);
                stems += stemDelta.StemCount;
                (accounts, slots, lastAddress) = (batch.Accounts, batch.Slots, batch.LastAddress);

                double secs = sinceLog.Elapsed.TotalSeconds;
                if (secs > 0)
                {
                    accountsPerSec = (accounts - loggedAccounts) / secs;
                    slotsPerSec = (slots - loggedSlots) / secs;
                    stemsPerSec = (stems - loggedStems) / secs;
                }
                (loggedAccounts, loggedSlots, loggedStems) = (accounts, slots, stems);
                sinceLog.Restart();

                progress.Update((ulong)(accounts + slots));
                progress.LogProgress();
            }
        }

        Task flusher = Task.Run(async () =>
        {
            try { await FlushLoop(); }
            catch { pipelineCts.Cancel(); throw; } // unblock a consumer parked on the full channel
        });

        Dictionary<Stem, Dictionary<byte, ValueHash256>> window = new(FlushEntryInterval);
        IPbtPersistence.IWriteBatch? writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.DisableWAL);
        int pending = 0;
        long accounts = 0, slots = 0;
        Address lastAddress = Address.Zero;

        try
        {
            await foreach (RebuildEntry entry in source.ReadAllAsync(pipelineCts.Token))
            {
                if (entry.IsAccount)
                {
                    AddAccount(entry.Address, entry.Account!, entry.Code, window, writeBatch!);
                    accounts++;
                    lastAddress = entry.Address;
                }
                else
                {
                    AddSlot(entry.Address, entry.Slot, entry.Value!, window, writeBatch!);
                    slots++;
                }

                if (++pending >= FlushEntryInterval)
                {
                    await flushChannel.Writer.WriteAsync(new FlushBatch(window, writeBatch!, accounts, slots, lastAddress), pipelineCts.Token);
                    window = new(FlushEntryInterval);
                    writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.DisableWAL);
                    pending = 0;
                }
            }

            // seal the final (possibly empty) window; the flusher owns its write batch from here
            await flushChannel.Writer.WriteAsync(new FlushBatch(window, writeBatch!, accounts, slots, lastAddress), pipelineCts.Token);
            writeBatch = null;
            flushChannel.Writer.Complete();
            await flusher;
        }
        catch
        {
            flushChannel.Writer.TryComplete();
            writeBatch?.Dispose(); // the un-sealed batch we still own, if any
            await flusher; // if the flusher faulted, this rethrows its (root-cause) exception
            throw; // otherwise our own failure
        }

        // the data windows skipped the WAL, so make them durable before the pointer that claims them
        target.Flush();

        // atomically advance the persisted-state pointer to the rebuilt state
        using (target.CreateWriteBatch(StateId.PreGenesis, new StateId(blockNumber, root), WriteFlags.None)) { }

        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at block {blockNumber}: {accounts} accounts, {slots} slots, {stems} stems, root {root}");
        return root;
    }

    /// <summary>A full window handed from the consumer to the flush worker: its dirty leaves, the write batch its flat rows are staged in, and the progress counters as of when it was sealed.</summary>
    private readonly record struct FlushBatch(
        Dictionary<Stem, Dictionary<byte, ValueHash256>> Window,
        IPbtPersistence.IWriteBatch WriteBatch,
        long Accounts,
        long Slots,
        Address LastAddress);

    /// <summary>
    /// Folds the accumulated window into the tree on top of <paramref name="currentRoot"/>, commits it,
    /// and clears the window. <paramref name="stemDelta"/> reports the change this window makes to the
    /// tree's stem count (zero for an empty window).
    /// </summary>
    private ValueHash256 FlushAndCommit(IPbtPersistence.IWriteBatch writeBatch, ValueHash256 currentRoot, Dictionary<Stem, Dictionary<byte, ValueHash256>> window, out PbtSubtreeStats stemDelta)
    {
        stemDelta = default;
        if (window.Count > 0)
        {
            using PbtWriteBatch changes = new(window.Count, buckets: null);
            foreach ((Stem stem, Dictionary<byte, ValueHash256> leaves) in window)
            {
                IPbtStemChanges stemChanges = PbtStemChanges.Rent();
                foreach ((byte subIndex, ValueHash256 leaf) in leaves) stemChanges = stemChanges.Set(subIndex, leaf);
                changes.Add(stem, stemChanges);
            }

            // a fresh reader sees the previously committed windows; the updater reads their prior nodes
            // and blobs and writes the new ones into this window's still-open batch
            using IPbtPersistence.IReader reader = target.CreateReader();
            PersistenceBackedPbtStore store = new(reader, writeBatch);
            currentRoot = TrieUpdater.UpdateRoot(store, currentRoot, changes, PooledRefCountingMemoryProvider.Instance, config.TrieNodeWriteFormat(), out stemDelta);
        }

        writeBatch.Dispose(); // atomic commit of this window's flat rows and, when non-empty, its leaves and nodes
        window.Clear();
        return currentRoot;
    }

    /// <summary>Adds an account's flat row and its EIP-8297 header leaves (BASIC_DATA, CODE_HASH, header and overflow code chunks).</summary>
    private static void AddAccount(Address address, Account account, byte[]? code, Dictionary<Stem, Dictionary<byte, ValueHash256>> window, IPbtPersistence.IWriteBatch writeBatch)
    {
        writeBatch.SetAccount(address, account);

        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);
        Span<byte> basicData = stackalloc byte[32];
        PbtKeyDerivation.PackBasicData(basicData, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
        SetLeaf(window, headerStem, PbtKeyDerivation.BasicDataLeafKey, ToLeaf(basicData));
        SetLeaf(window, headerStem, PbtKeyDerivation.CodeHashLeafKey, ToLeaf(account.CodeHash.Bytes));

        if (code is not { Length: > 0 }) return;

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        for (int i = 0; i < headerChunks; i++)
        {
            SetLeaf(window, headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), ToLeaf(Chunk(chunks, i)));
        }

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount; i++)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            SetLeaf(window, overflowStem, subIndex, ToLeaf(Chunk(chunks, i)));
        }
    }

    /// <summary>Adds a slot's flat row and its EIP-8297 leaf, routed to the account header (index &lt; 64) or a storage-zone stem.</summary>
    private static void AddSlot(Address address, in UInt256 slot, byte[] value, Dictionary<Stem, Dictionary<byte, ValueHash256>> window, IPbtPersistence.IWriteBatch writeBatch)
    {
        EvmWord word = EvmWordSlot.FromStripped(value);
        writeBatch.SetSlot(address, slot, word);

        if (PbtKeyDerivation.IsHeaderSlot(slot))
        {
            SetLeaf(window, PbtKeyDerivation.AccountHeaderStem(address), PbtKeyDerivation.HeaderSlotSubIndex(slot), SlotLeaf(word));
        }
        else
        {
            Stem stem = PbtKeyDerivation.StorageStem(address, slot, out byte subIndex);
            SetLeaf(window, stem, subIndex, SlotLeaf(word));
        }
    }

    private static ReadOnlySpan<byte> Chunk(byte[] chunks, int chunkId) =>
        chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);

    private static void SetLeaf(Dictionary<Stem, Dictionary<byte, ValueHash256>> window, in Stem stem, byte subIndex, in ValueHash256 leaf)
    {
        ref Dictionary<byte, ValueHash256>? leaves = ref CollectionsMarshal.GetValueRefOrAddDefault(window, stem, out _);
        leaves ??= [];
        leaves[subIndex] = leaf;
    }

    private static ValueHash256 ToLeaf(ReadOnlySpan<byte> value)
    {
        ValueHash256 leaf = default;
        value.CopyTo(leaf.BytesAsSpan);
        return leaf;
    }

    private static ValueHash256 SlotLeaf(in EvmWord value) =>
        EvmWordSlot.IsZero(value) ? default : new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));
}
