// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nethermind.Core;
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
/// written <see cref="StateId.PreGenesis"/> → <see cref="StateId.PreGenesis"/> — the persisted-state
/// pointer stays pre-genesis so a crash mid-rebuild leaves the state unpopulated and the next run
/// restarts cleanly; only a final empty batch atomically advances the pointer to the rebuilt state.
/// Because each window folds against the previously committed windows, a stem split across windows is
/// merged correctly (the updater reads its prior leaf blob and folds the new leaves in).
/// </remarks>
public sealed class PbtRebuilder(IPbtPersistence target, ILogManager logManager)
{
    /// <summary>Entries (accounts + slots) buffered before a window is folded into the tree and committed.</summary>
    internal int FlushEntryInterval { get; init; } = 128_000;

    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    /// <summary>Rebuilds the tree from <paramref name="source"/> and returns the EIP-8297 root at <paramref name="blockNumber"/>.</summary>
    public async Task<ValueHash256> Rebuild(ChannelReader<RebuildEntry> source, ulong blockNumber, CancellationToken cancellationToken)
    {
        ValueHash256 currentRoot = default; // empty tree is 32 zero bytes
        Dictionary<Stem, Dictionary<byte, ValueHash256>> window = new(FlushEntryInterval);
        IPbtPersistence.IWriteBatch writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis);
        int pending = 0;
        long accounts = 0, slots = 0;

        try
        {
            await foreach (RebuildEntry entry in source.ReadAllAsync(cancellationToken))
            {
                if (entry.IsAccount)
                {
                    AddAccount(entry.Address, entry.Account!, entry.Code, window, writeBatch);
                    accounts++;
                }
                else
                {
                    AddSlot(entry.Address, entry.Slot, entry.Value!, window, writeBatch);
                    slots++;
                }

                if (++pending >= FlushEntryInterval)
                {
                    currentRoot = FlushAndCommit(writeBatch, currentRoot, window);
                    writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis);
                    pending = 0;
                    if (_logger.IsInfo) _logger.Info($"PBT rebuild progress: {accounts} accounts, {slots} slots");
                }
            }

            // last (possibly partial) window
            currentRoot = FlushAndCommit(writeBatch, currentRoot, window);
        }
        catch
        {
            writeBatch.Dispose();
            throw;
        }

        // atomically advance the persisted-state pointer to the rebuilt state
        using (target.CreateWriteBatch(StateId.PreGenesis, new StateId(blockNumber, currentRoot))) { }

        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at block {blockNumber}: {accounts} accounts, {slots} slots, root {currentRoot}");
        return currentRoot;
    }

    /// <summary>Folds the accumulated window into the tree on top of <paramref name="currentRoot"/>, commits it, and clears the window.</summary>
    private ValueHash256 FlushAndCommit(IPbtPersistence.IWriteBatch writeBatch, ValueHash256 currentRoot, Dictionary<Stem, Dictionary<byte, ValueHash256>> window)
    {
        if (window.Count > 0)
        {
            using PbtWriteBatch changes = new(window.Count);
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
            currentRoot = TrieUpdater.UpdateRoot(store, currentRoot, changes);
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

        byte[][] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int headerChunks = Math.Min(chunks.Length, PbtKeyDerivation.HeaderCodeChunks);
        for (int i = 0; i < headerChunks; i++)
        {
            SetLeaf(window, headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), ToLeaf(chunks[i]));
        }

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            SetLeaf(window, overflowStem, subIndex, ToLeaf(chunks[i]));
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
