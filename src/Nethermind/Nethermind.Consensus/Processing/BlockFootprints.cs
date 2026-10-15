// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The footprints of one block's warm runs, by transaction index.</summary>
/// <remarks>
/// The storage writes and reads of the footprints are indexed by slot, and their account changes and the accounts they
/// depend on by address, so a footprint that depends on state an earlier transaction changes is known to be invalidated
/// and can be refreshed: run again on the state the earlier footprints leave. Block processing reports the writes of the
/// transactions it executes, which take the place of what their footprints predicted.
/// </remarks>
internal sealed class BlockFootprints(Block block)
{
    /// <summary>
    /// Footprints indexed for one slot's writes, and for its reads. Past it, a slot's writes are no longer tracked and the
    /// footprints that read it are not refreshed; a reader past it is only not invalidated through that slot.
    /// </summary>
    internal const int MaxIndexedPerSlot = int.MaxValue;

    /// <summary>Refreshes one block may run.</summary>
    internal const int MaxRefreshesPerBlock = int.MaxValue;

    /// <summary>
    /// Once the refreshes the warm pass left are done, only the transactions this close ahead of block processing are
    /// refreshed: the ones further on are refreshed as it nears them, on the state it has reached by then.
    /// </summary>
    internal const int RefreshWindow = 64;

    // A reader of an account that is not a footprint's precondition: the sender of a transaction whose warm run could not
    // pay for it.
    private const int UnfundedSender = -1;

    private readonly Transaction[] _transactions = block.Transactions;
    private readonly Dictionary<AddressAsKey, Slot> _accounts = [];
    // The last position that depends on each account, so block processing can tell which accounts it must report.
    private readonly ConcurrentDictionary<AddressAsKey, int> _lastAccountReader = new();
    private readonly bool[] _unfunded = new bool[block.Transactions.Length];
    private volatile bool _windowed;
    private int _workers = 1;

    // Writes times effects past which block processing wakes the refresh worker rather than compare: comparing would
    // cost more than the wake.
    private const int MaxComparisons = 256;

    private readonly Hash256? _blockHash = block.Hash;
    private readonly TransactionFootprint?[] _footprints = new TransactionFootprint?[block.Transactions.Length];

    private readonly Lock _lock = new();
    // The writes and reads of each slot point into what is indexed at their position: the footprint stored there, or the
    // writes block processing reported for it, so indexing a footprint copies none of it.
    private readonly Dictionary<StorageCell, Slot> _slots = [];
    private readonly Indexed[] _indexed = new Indexed[block.Transactions.Length];
    private readonly bool[] _executed = new bool[block.Transactions.Length];
    private readonly ulong[] _invalidated = new ulong[(block.Transactions.Length + 63) >> 6];
    private int _invalidatedCount;
    private int _untrackedCount;
    private int _refreshesRun;
    private int _writesVersion;
    private int _visit;

    // Written by block processing, applied by the refresh workers, so block processing never takes the lock.
    private readonly ConcurrentQueue<(int Position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, ExecutedAccount[]? Accounts)> _reported = new();
    private readonly SemaphoreSlim _changed = new(0);
    private volatile bool _warmPassEnded;
    private volatile bool _waitsForBlockProcessing = true;

    /// <remarks>
    /// Needs receipts without a per-transaction state root (EIP-658). EIP-8037 gas accounting and a block access list
    /// (EIP-7928) are built while transactions execute, which a replay does not.
    /// </remarks>
    public static bool AppliesTo(Block block, IReleaseSpec spec) =>
        block.Transactions.Length > 0 && AppliesTo(spec) && block.BlockAccessList is null;

    /// <inheritdoc cref="AppliesTo(Block, IReleaseSpec)"/>
    public static bool AppliesTo(IReleaseSpec spec) =>
        spec.IsEip658Enabled && !spec.IsEip8037Enabled && !spec.BlockLevelAccessListsEnabled;

    /// <remarks>
    /// A warm run skips the pre-execution checks; the nonce is checked when it is recorded and the rest when it is
    /// replayed, except a nonce that would overflow (EIP-2681) and a zero fee, which are excluded here.
    /// </remarks>
    public static bool IsRecordable(Transaction tx) =>
        tx.SenderAddress is not null
        && !tx.SupportsFrames
        && !tx.IsSystem()
        && tx.Nonce != ulong.MaxValue
        && !(tx.MaxFeePerGas.IsZero && tx.MaxPriorityFeePerGas.IsZero);

    public int Count => _footprints.Length;

    public TransactionFootprint? Get(int index) => Volatile.Read(ref _footprints[index]);

    /// <param name="seededAt">For a refreshed footprint, the <see cref="WritesVersion"/> its run was seeded at.</param>
    public void Store(int index, TransactionFootprint footprint, int seededAt = -1)
    {
        Volatile.Write(ref _footprints[index], footprint);
        Index(index, footprint, seededAt);
    }

    /// <summary>Changes whenever writes the footprints leave are replaced.</summary>
    public int WritesVersion => Volatile.Read(ref _writesVersion);

    /// <summary>Refreshes the block has run.</summary>
    public int RefreshesRun
    {
        get
        {
            lock (_lock) return _refreshesRun;
        }
    }

    /// <summary>The footprint of <paramref name="tx"/>, at <paramref name="index"/> in the block <paramref name="header"/> heads.</summary>
    public TransactionFootprint? Find(int index, Transaction tx, BlockHeader header)
    {
        TransactionFootprint?[] footprints = _footprints;
        if ((uint)index >= (uint)footprints.Length || _blockHash is null || header.Hash != _blockHash) return null;
        TransactionFootprint? footprint = Volatile.Read(ref footprints[index]);
        return footprint is not null && ReferenceEquals(footprint.Transaction, tx) ? footprint : null;
    }

    /// <summary>
    /// The value the footprints before <paramref name="position"/> leave <paramref name="cell"/> at; null when none
    /// writes it, so it is at its parent value.
    /// </summary>
    public UInt256? ValueBefore(in StorageCell cell, int position)
    {
        lock (_lock)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
            return Unsafe.IsNullRef(ref slot) || slot.Untracked ? null : ValueBefore(in slot.Writers, position);
        }
    }

    /// <summary>Whether the footprint read a slot too many transactions write for its writes to be tracked.</summary>
    public bool ReadsUntrackedSlot(TransactionFootprint footprint)
    {
        lock (_lock)
        {
            if (_untrackedCount == 0) return false;
            foreach (ref readonly SlotPrecondition read in footprint.Slots)
            {
                ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, read.Cell);
                if (!Unsafe.IsNullRef(ref slot) && slot.Untracked) return true;
            }

            return false;
        }
    }

    /// <summary>Takes the first invalidated position after <paramref name="after"/>, dropping the ones before it.</summary>
    public bool TryTakeInvalidated(int after, out int position) => TryTakeInvalidated(after, int.MaxValue, out position);

    /// <summary>
    /// Takes the first invalidated position after <paramref name="after"/> and before <paramref name="limit"/>, dropping
    /// the ones before it.
    /// </summary>
    public bool TryTakeInvalidated(int after, int limit, out int position)
    {
        lock (_lock)
        {
            if (_invalidatedCount > 0 && _refreshesRun < MaxRefreshesPerBlock)
            {
                int first = after + 1;
                int word = first >> 6;
                for (int i = 0; i < word && i < _invalidated.Length; i++) Drop(i, ulong.MaxValue);
                if (word < _invalidated.Length) Drop(word, (1UL << (first & 63)) - 1);
                for (; word < _invalidated.Length; word++)
                {
                    if (_invalidated[word] == 0) continue;
                    position = (word << 6) + BitOperations.TrailingZeroCount(_invalidated[word]);
                    if (position >= limit) break;
                    _invalidated[word] &= _invalidated[word] - 1;
                    _invalidatedCount--;
                    return true;
                }
            }
        }

        position = -1;
        return false;
    }

    /// <summary>Whether a position is waiting for a refresh, wherever it is.</summary>
    public bool HasInvalidated
    {
        get
        {
            lock (_lock) return _invalidatedCount > 0;
        }
    }

    /// <summary>Whether refreshes are only taken within <see cref="RefreshWindow"/> of block processing.</summary>
    public bool Windowed => _windowed;

    /// <summary>Called once the refreshes the warm pass left are done.</summary>
    public void EnterWindow() => _windowed = true;

    /// <summary>The refresh workers there are to wake when the pass ends.</summary>
    public void SetWorkers(int workers) => _workers = Math.Max(1, workers);

    /// <summary>Wakes another refresh worker.</summary>
    public void Wake() => _changed.Release();

    /// <summary>Counts a refresh that runs; once the block has run its refreshes, no more are taken.</summary>
    public void CountRefresh()
    {
        lock (_lock) _refreshesRun++;
    }

    /// <summary>Whether a refresh or a write block processing reported is waiting.</summary>
    public bool HasWork
    {
        get
        {
            if (!_reported.IsEmpty) return true;
            lock (_lock) return _invalidatedCount > 0 && _refreshesRun < MaxRefreshesPerBlock;
        }
    }

    public bool WarmPassEnded => _warmPassEnded;

    public void EndWarmPass()
    {
        _warmPassEnded = true;
        _changed.Release(_workers);
    }

    /// <summary>Whether the refresh worker waits for the writes block processing reports once it has nothing left.</summary>
    public bool WaitsForBlockProcessing => _waitsForBlockProcessing;

    /// <summary>For a pass waited on before block processing: the refresh worker ends once it has nothing left.</summary>
    public void StopWaitingForBlockProcessing()
    {
        _waitsForBlockProcessing = false;
        _changed.Release(_workers);
    }

    /// <summary>
    /// Waits until a footprint is invalidated, block processing reports writes, the warm pass ends or the pass stops
    /// waiting for block processing.
    /// </summary>
    public void WaitForWork(CancellationToken token) => _changed.Wait(token);

    /// <inheritdoc cref="WaitForWork(CancellationToken)"/>
    /// <param name="millisecondsTimeout">How long to wait at most, for a worker that polls block processing's progress.</param>
    public void WaitForWork(int millisecondsTimeout, CancellationToken token) => _changed.Wait(millisecondsTimeout, token);

    /// <summary>For tests: the wakes the refresh worker has not taken.</summary>
    internal int PendingWakes => _changed.CurrentCount;

    /// <summary>
    /// Queues the storage writes block processing committed when it executed the transaction at <paramref name="position"/>;
    /// empty when it wrote none.
    /// </summary>
    /// <remarks>
    /// Writes its footprint predicted invalidate nothing, so they wait for the refresh worker's next wake: waking a
    /// blocked worker costs block processing microseconds.
    /// </remarks>
    public void QueueExecuted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes, ExecutedAccount[]? accounts = null)
    {
        _reported.Enqueue((position, writes, accounts));
        if (accounts is not null || !WerePredicted(position, writes)) _changed.Release();
    }

    /// <summary>Whether a transaction after <paramref name="position"/> depends on <paramref name="address"/>.</summary>
    /// <remarks>Called by block processing; takes no lock.</remarks>
    public bool WatchesAccount(Address address, int position) =>
        _lastAccountReader.TryGetValue(address, out int last) && last > position;

    [SkipLocalsInit]
    private bool WerePredicted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes)
    {
        TransactionFootprint?[] footprints = _footprints;
        TransactionFootprint? footprint = (uint)position < (uint)footprints.Length ? Volatile.Read(ref footprints[position]) : null;
        int count = writes.Length;
        if ((footprint?.StorageWrites ?? 0) != count) return false;
        if (count == 0) return true;
        ReadOnlySpan<StateEffect> effects = footprint!.Effects;
        if (count * effects.Length > MaxComparisons) return false;
        // A footprint writes each slot once, and a commit reports each slot once.
        foreach ((StorageCell cell, UInt256 value) in writes.Span)
        {
            if (!Writes(effects, in cell, in value)) return false;
        }

        return true;
    }

    private static bool Writes(ReadOnlySpan<StateEffect> effects, in StorageCell cell, in UInt256 value)
    {
        foreach (ref readonly StateEffect effect in effects)
        {
            if (effect.Kind == EffectKind.SetStorage && effect.Value == value && effect.Cell.Equals(in cell)) return true;
        }

        return false;
    }

    /// <summary>Puts the writes block processing reported in place of what their footprints predicted, in the order it executed them.</summary>
    public void ApplyExecuted()
    {
        while (_reported.TryDequeue(out (int Position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, ExecutedAccount[]? Accounts) executed))
        {
            ApplyExecuted(executed.Position, executed.Writes, executed.Accounts);
        }
    }

    private void ApplyExecuted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes, ExecutedAccount[]? accounts)
    {
        if ((uint)position >= (uint)_footprints.Length) return;
        bool invalidated;
        lock (_lock)
        {
            // The transaction is done: its reads no longer invalidate it, and its writes are the ones it made.
            _executed[position] = true;
            ForgetUnfunded(position);
            invalidated = Replace(position, Indexed.OfExecuted(writes, accounts, _indexed[position]));
        }

        if (invalidated) _changed.Release();
    }

    /// <summary>
    /// Notes that the warm run of the transaction at <paramref name="position"/> could not pay for it: it is run again
    /// once an earlier transaction changes its sender.
    /// </summary>
    public void MarkUnfunded(int position)
    {
        if ((uint)position >= (uint)_transactions.Length || _transactions[position].SenderAddress is not { } sender) return;
        bool invalidated = false;
        lock (_lock)
        {
            if (_executed[position] || _unfunded[position] || _indexed[position].Footprint is not null) return;
            _unfunded[position] = true;
            ref Slot account = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, sender, out _);
            account.Readers.Add(new Entry(position, UnfundedSender));
            NoteAccountReader(sender, position);
            if (HasChangeBefore(in account.Writers, position, sender, unfunded: true)) invalidated = Invalidate(position);
        }

        if (invalidated) _changed.Release();
    }

    /// <summary>Whether the transaction at <paramref name="position"/> has no footprint because its warm run could not pay for it.</summary>
    public bool IsUnfunded(int position)
    {
        lock (_lock) return (uint)position < (uint)_unfunded.Length && _unfunded[position];
    }

    private void ForgetUnfunded(int position)
    {
        if (!_unfunded[position]) return;
        _unfunded[position] = false;
        ref Slot account = ref CollectionsMarshal.GetValueRefOrNullRef(_accounts, _transactions[position].SenderAddress!);
        if (!Unsafe.IsNullRef(ref account)) account.Readers.Remove(position);
    }

    private void NoteAccountReader(Address address, int position) =>
        _lastAccountReader.AddOrUpdate(address, static (_, position) => position, static (_, last, position) => Math.Max(last, position), position);

    private void Index(int position, TransactionFootprint footprint, int seededAt)
    {
        bool invalidated;
        lock (_lock)
        {
            // A run that ends after block processing executed its transaction does not replace the writes it made.
            if (_executed[position]) return;
            ForgetUnfunded(position);
            // With no write replaced since the refresh seeded the slots the footprint before it read, a refreshed run
            // that read one of them at another value than its seed changed it itself first (a creation clears the
            // storage), and running it again reads the same.
            bool unchangedSinceSeed = seededAt == _writesVersion;
            TransactionFootprint? seeded = unchangedSinceSeed ? _indexed[position].Footprint : null;
            invalidated = Replace(position, Indexed.Of(footprint));

            ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
            bool outdated = false;
            for (int i = 0; i < reads.Length; i++)
            {
                ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, reads[i].Cell, out _);
                if (slot.Untracked) continue;
                outdated |= ValueBefore(in slot.Writers, position) is { } value && value != reads[i].Value && (seeded is null || !Reads(seeded, reads[i].Cell));
                if (slot.Readers.Count < MaxIndexedPerSlot) slot.Readers.Add(new Entry(position, i));
            }

            // A refreshed run started from the accounts the footprints before it leave; any other run from the parent's,
            // with only its sender's earlier transactions applied.
            ReadOnlySpan<AccountPrecondition> accounts = footprint.Accounts;
            Address sender = footprint.Transaction.SenderAddress!;
            for (int i = 0; i < accounts.Length; i++)
            {
                ref readonly AccountPrecondition precondition = ref accounts[i];
                if (precondition.Fields == AccountFields.None) continue;
                ref Slot account = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, precondition.Address, out _);
                account.Readers.Add(new Entry(position, i));
                NoteAccountReader(precondition.Address, position);
                if (!outdated && !(footprint.Refreshed && unchangedSinceSeed))
                {
                    outdated = AffectedBefore(in account.Writers, position, in precondition, footprint.Refreshed ? null : sender);
                }
            }

            if (outdated) invalidated |= Invalidate(position);
        }

        if (invalidated) _changed.Release();
    }

    // Puts what the position now writes, from a refreshed footprint or the transaction executed there, in place of what
    // was indexed for it, and marks the later readers of the slots and accounts either changes. A slot written many
    // times is handled once per pass; its entry keeps the last write.
    private bool Replace(int position, in Indexed source)
    {
        Indexed previous = _indexed[position];
        Unindex(position, in previous);
        _indexed[position] = source;
        _writesVersion++;
        foreach ((StorageCell cell, int index) in source.Writes)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, cell, out _);
            if (!slot.Untracked && !slot.Writers.Put(new Entry(position, index), MaxIndexedPerSlot)) Untrack(ref slot);
        }

        AccountChange[] changes = source.Accounts;
        for (int i = 0; i < changes.Length; i++)
        {
            ref Slot account = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, changes[i].Address, out _);
            account.Writers.Put(new Entry(position, i), int.MaxValue);
        }

        int visit = ++_visit;
        bool invalidated = false;
        foreach ((StorageCell cell, _) in source.Writes) invalidated |= InvalidateReaders(in cell, position, visit);
        foreach ((StorageCell cell, _) in previous.Writes) invalidated |= InvalidateReaders(in cell, position, visit);

        Address? writer = _transactions[position].SenderAddress;
        AccountChange[] before = previous.Accounts;
        foreach (AccountChange change in changes)
        {
            AccountChange? old = Find(before, change.Address);
            if (old == change) continue;
            invalidated |= InvalidateAccountReaders(change.Address, position, change.Kind | (old?.Kind ?? AccountChangeKind.None), writer, visit);
        }

        foreach (AccountChange change in before)
        {
            if (Find(changes, change.Address) is null) invalidated |= InvalidateAccountReaders(change.Address, position, change.Kind, writer, visit);
        }

        return invalidated;
    }

    private void Unindex(int position, in Indexed previous)
    {
        int visit = ++_visit;
        foreach ((StorageCell cell, _) in previous.Writes)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
            if (Unsafe.IsNullRef(ref slot) || slot.Visited == visit) continue;
            slot.Visited = visit;
            slot.Writers.Remove(position);
        }

        foreach (AccountChange change in previous.Accounts)
        {
            ref Slot account = ref CollectionsMarshal.GetValueRefOrNullRef(_accounts, change.Address);
            if (!Unsafe.IsNullRef(ref account)) account.Writers.Remove(position);
        }

        if (previous.Footprint is not { } footprint) return;
        foreach (ref readonly SlotPrecondition read in footprint.Slots)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, read.Cell);
            if (!Unsafe.IsNullRef(ref slot)) slot.Readers.Remove(position);
        }

        foreach (ref readonly AccountPrecondition read in footprint.Accounts)
        {
            if (read.Fields == AccountFields.None) continue;
            ref Slot account = ref CollectionsMarshal.GetValueRefOrNullRef(_accounts, read.Address);
            if (!Unsafe.IsNullRef(ref account)) account.Readers.Remove(position);
        }
    }

    private static AccountChange? Find(AccountChange[] changes, Address address)
    {
        foreach (AccountChange change in changes)
        {
            if (change.Address == address) return change;
        }

        return null;
    }

    // Marks the later footprints that depend on what the change to the account touches. A run that is not a refresh
    // started from the parent's accounts with its sender's earlier transactions applied, so a change those made was
    // already there, unless block processing reports it.
    private bool InvalidateAccountReaders(Address address, int position, AccountChangeKind kind, Address? writer, int visit)
    {
        ref Slot account = ref CollectionsMarshal.GetValueRefOrNullRef(_accounts, address);
        if (Unsafe.IsNullRef(ref account) || account.Visited == visit) return false;
        account.Visited = visit;
        bool invalidated = false;
        for (int i = 0; i < account.Readers.Count; i++)
        {
            Entry reader = account.Readers[i];
            if (reader.Position <= position) continue;
            if (reader.Index == UnfundedSender)
            {
                if (writer != address && (kind & (AccountChangeKind.Credit | AccountChangeKind.Absolute | AccountChangeKind.Create | AccountChangeKind.MayCreate)) != 0)
                {
                    invalidated |= Invalidate(reader.Position);
                }

                continue;
            }

            TransactionFootprint footprint = _indexed[reader.Position].Footprint!;
            if (!footprint.Refreshed && (kind & AccountChangeKind.Absolute) == 0 && writer is not null && footprint.Transaction.SenderAddress == writer) continue;
            if (Affects(kind, in footprint.Accounts[reader.Index])) invalidated |= Invalidate(reader.Position);
        }

        return invalidated;
    }

    // Whether a change before the position, other than the sender's own unreported ones, touches what the precondition depends on.
    private bool AffectedBefore(in Entries writers, int position, in AccountPrecondition precondition, Address? sender)
    {
        int end = writers.FirstAtOrAfter(position);
        for (int w = 0; w < end; w++)
        {
            Entry writer = writers[w];
            AccountChangeKind kind = _indexed[writer.Position].Accounts[writer.Index].Kind;
            if (sender is not null && (kind & AccountChangeKind.Absolute) == 0 && _transactions[writer.Position].SenderAddress == sender) continue;
            if (Affects(kind, in precondition)) return true;
        }

        return false;
    }

    private bool HasChangeBefore(in Entries writers, int position, Address sender, bool unfunded)
    {
        int end = writers.FirstAtOrAfter(position);
        for (int w = 0; w < end; w++)
        {
            Entry writer = writers[w];
            if (_transactions[writer.Position].SenderAddress == sender) continue;
            AccountChangeKind kind = _indexed[writer.Position].Accounts[writer.Index].Kind;
            if (!unfunded || (kind & (AccountChangeKind.Credit | AccountChangeKind.Absolute | AccountChangeKind.Create | AccountChangeKind.MayCreate)) != 0) return true;
        }

        return false;
    }

    /// <summary>Whether a change of <paramref name="kind"/> can break what <paramref name="precondition"/> requires.</summary>
    internal static bool Affects(AccountChangeKind kind, in AccountPrecondition precondition)
    {
        AccountFields fields = precondition.Fields;
        if (fields == AccountFields.None || kind == AccountChangeKind.None) return false;
        if ((kind & (AccountChangeKind.Absolute | AccountChangeKind.Create | AccountChangeKind.Delete)) != 0) return true;
        if ((fields & AccountFields.Nonce) != 0 && (kind & AccountChangeKind.Nonce) != 0) return true;
        if ((fields & AccountFields.Code) != 0 && (kind & AccountChangeKind.Code) != 0) return true;
        if ((fields & AccountFields.Balance) != 0 && (kind & (AccountChangeKind.Credit | AccountChangeKind.Debit)) != 0) return true;
        // A credit never takes a balance below a minimum.
        if ((fields & AccountFields.MinimumBalance) != 0 && (kind & AccountChangeKind.Debit) != 0) return true;
        if ((fields & AccountFields.Liveness) != 0
            && (precondition.IsDead
                ? (kind & (AccountChangeKind.Credit | AccountChangeKind.Nonce | AccountChangeKind.Code | AccountChangeKind.MayCreate)) != 0
                : (kind & AccountChangeKind.Debit) != 0))
        {
            return true;
        }

        return (fields & AccountFields.Existence) != 0 && !precondition.Exists && (kind & AccountChangeKind.MayCreate) != 0;
    }

    /// <summary>
    /// Brings <paramref name="address"/> in <paramref name="state"/>, at the block's parent, to what the transactions
    /// before <paramref name="position"/> leave it at: the last one block processing executed, then the changes of the
    /// footprints after it.
    /// </summary>
    /// <returns>Whether any change was applied.</returns>
    public bool SeedAccount(IWorldState state, IReleaseSpec spec, Address address, int position)
    {
        ExecutedAccount? executed = null;
        ArrayPoolList<TransactionFootprint>? applied = null;
        try
        {
            lock (_lock)
            {
                ref Slot account = ref CollectionsMarshal.GetValueRefOrNullRef(_accounts, address);
                if (Unsafe.IsNullRef(ref account)) return false;
                int end = account.Writers.FirstAtOrAfter(position);
                int start = 0;
                for (int w = end - 1; w >= 0; w--)
                {
                    Entry writer = account.Writers[w];
                    if (_indexed[writer.Position].ExecutedAccount(writer.Index) is { } reported)
                    {
                        executed = reported;
                        start = w + 1;
                        break;
                    }
                }

                for (int w = start; w < end; w++)
                {
                    if (_indexed[account.Writers[w].Position].Effects is { } footprint) (applied ??= new ArrayPoolList<TransactionFootprint>(end - start)).Add(footprint);
                }
            }

            if (executed is null && applied is null) return false;
            if (executed is { } at) SetAccount(state, spec, in at);
            if (applied is not null)
            {
                foreach (TransactionFootprint footprint in applied)
                {
                    foreach (ref readonly StateEffect effect in footprint.Effects)
                    {
                        if (effect.Kind != EffectKind.SetStorage && effect.Address == address) effect.Replay(state, spec);
                    }
                }
            }

            return true;
        }
        finally
        {
            applied?.Dispose();
        }
    }

    private static void SetAccount(IWorldState state, IReleaseSpec spec, in ExecutedAccount account)
    {
        Address address = account.Address;
        if (!account.Exists)
        {
            if (state.AccountExists(address)) state.DeleteAccount(address);
            return;
        }

        if (!state.AccountExists(address))
        {
            UInt256 balance = account.Balance;
            state.CreateAccount(address, in balance, account.Nonce);
            return;
        }

        UInt256 current = state.GetBalance(address);
        if (current < account.Balance) state.AddToBalance(address, account.Balance - current, spec, out _);
        else if (current > account.Balance) state.SubtractFromBalance(address, current - account.Balance, spec, out _);
        if (state.GetNonce(address) != account.Nonce) state.SetNonce(address, account.Nonce);
    }

    // A slot too many footprints write is no longer tracked: its writes and its readers leave the index.
    private void Untrack(ref Slot slot)
    {
        slot.Untracked = true;
        slot.Writers.Clear();
        slot.Readers.Clear();
        _untrackedCount++;
    }

    // Marks the later footprints that read the slot at a value other than the one the block now leaves before them. With
    // no earlier write left, the slot is back at its parent value, which only a refresh reads: its readers are marked.
    private bool InvalidateReaders(in StorageCell cell, int position, int visit)
    {
        ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
        if (Unsafe.IsNullRef(ref slot) || slot.Visited == visit) return false;
        slot.Visited = visit;
        bool invalidated = false;
        for (int i = 0; i < slot.Readers.Count; i++)
        {
            Entry reader = slot.Readers[i];
            if (reader.Position > position && (ValueBefore(in slot.Writers, reader.Position) is not { } value || value != ReadValue(reader)))
            {
                invalidated |= Invalidate(reader.Position);
            }
        }

        return invalidated;
    }

    private bool Invalidate(int position)
    {
        ref ulong word = ref _invalidated[position >> 6];
        ulong bit = 1UL << (position & 63);
        if ((word & bit) != 0) return false;
        word |= bit;
        _invalidatedCount++;
        return true;
    }

    private void Drop(int word, ulong mask)
    {
        ulong dropped = _invalidated[word] & mask;
        _invalidated[word] &= ~dropped;
        _invalidatedCount -= BitOperations.PopCount(dropped);
    }

    private UInt256? ValueBefore(in Entries writers, int position)
    {
        int at = writers.FirstAtOrAfter(position);
        return at == 0 ? null : WrittenValue(writers[at - 1]);
    }

    private UInt256 WrittenValue(Entry writer) => _indexed[writer.Position].WrittenValue(writer.Index);

    // A reader's position always has its footprint indexed: executed positions keep no readers.
    private UInt256 ReadValue(Entry reader) => _indexed[reader.Position].Footprint!.Slots[reader.Index].Value;

    private static bool Reads(TransactionFootprint footprint, in StorageCell cell)
    {
        foreach (ref readonly SlotPrecondition read in footprint.Slots)
        {
            if (read.Cell.Equals(cell)) return true;
        }

        return false;
    }

    // A position, and where in what is indexed there the slot's write or read is.
    private readonly record struct Entry(int Position, int Index);

    // What is indexed at a position: the footprint stored there, or the writes block processing reported for it.
    private readonly struct Indexed
    {
        private readonly TransactionFootprint? _footprint;
        private readonly ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> _executed;
        private readonly AccountChange[]? _accounts;
        // For an executed transaction: what block processing reported of the accounts at the same index in the changes.
        private readonly ExecutedAccount[]? _executedAccounts;
        // For an executed transaction: the footprint whose changes stand for those of the accounts it did not report.
        private readonly TransactionFootprint? _predicted;

        private Indexed(TransactionFootprint? footprint, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> executed,
            AccountChange[] accounts, ExecutedAccount[]? executedAccounts, TransactionFootprint? predicted)
        {
            _footprint = footprint;
            _executed = executed;
            _accounts = accounts;
            _executedAccounts = executedAccounts;
            _predicted = predicted;
        }

        public static Indexed Of(TransactionFootprint footprint) => new(footprint, default, AccountChange.Of(footprint), null, null);

        public static Indexed OfExecuted(ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes, ExecutedAccount[]? reported, in Indexed previous)
        {
            TransactionFootprint? predicted = previous.Footprint ?? previous._predicted;
            AccountChange[] stale = previous.Accounts;
            if (reported is null || reported.Length == 0) return new(null, writes, Unreported(stale, null, []), null, predicted);

            AccountChange[] accounts = Unreported(stale, reported, new AccountChange[reported.Length]);
            for (int i = 0; i < reported.Length; i++) accounts[i] = new AccountChange(reported[i].Address, AccountChangeKind.Absolute, default, default);
            return new(null, writes, accounts, reported, predicted);
        }

        // The reported accounts first, at their indexes, then the predicted changes of the others.
        private static AccountChange[] Unreported(AccountChange[] predicted, ExecutedAccount[]? reported, AccountChange[] first)
        {
            int kept = 0;
            foreach (AccountChange change in predicted)
            {
                if (!IsReported(reported, change.Address)) kept++;
            }

            if (kept == 0) return first;
            AccountChange[] accounts = new AccountChange[first.Length + kept];
            first.CopyTo(accounts, 0);
            int at = first.Length;
            foreach (AccountChange change in predicted)
            {
                if (!IsReported(reported, change.Address)) accounts[at++] = change;
            }

            return accounts;
        }

        private static bool IsReported(ExecutedAccount[]? reported, Address address)
        {
            if (reported is null) return false;
            foreach (ExecutedAccount account in reported)
            {
                if (account.Address == address) return true;
            }

            return false;
        }

        public TransactionFootprint? Footprint => _footprint;

        /// <summary>The footprint whose effects describe the accounts here that block processing did not report.</summary>
        public TransactionFootprint? Effects => _footprint ?? _predicted;

        public AccountChange[] Accounts => _accounts ?? [];

        public ExecutedAccount? ExecutedAccount(int index) =>
            _executedAccounts is { } reported && (uint)index < (uint)reported.Length ? reported[index] : null;

        public WrittenSlots Writes => new(_footprint, _executed);

        public UInt256 WrittenValue(int index) => _footprint is not null ? _footprint.Effects[index].Value : _executed.Span[index].Value;
    }

    private struct Slot
    {
        public Entries Writers;
        public Entries Readers;
        public bool Untracked;
        // The pass of Replace that last handled the slot.
        public int Visited;
    }

    // One slot's writers, in ascending position, or its readers: inline while there is one, in a list from the second.
    private struct Entries
    {
        private Entry _one;
        private List<Entry>? _many;
        private int _count;

        public readonly int Count => _count;

        public readonly Entry this[int i] => _many is null ? _one : _many[i];

        public void Add(Entry entry)
        {
            if (_many is not null) _many.Add(entry);
            else if (_count == 0) _one = entry;
            else _many = [_one, entry];
            _count++;
        }

        // Keeps ascending position and one entry per position; false when as many entries as allowed are taken.
        public bool Put(Entry entry, int capacity)
        {
            int at = FirstAtOrAfter(entry.Position);
            if (at < _count && this[at].Position == entry.Position)
            {
                if (_many is null) _one = entry;
                else _many[at] = entry;
                return true;
            }

            if (_count >= capacity) return false;
            if (_many is not null) _many.Insert(at, entry);
            else if (_count == 0) _one = entry;
            else _many = at == 0 ? [entry, _one] : [_one, entry];
            _count++;
            return true;
        }

        public void Remove(int position)
        {
            if (_many is null)
            {
                if (_count == 1 && _one.Position == position) _count = 0;
                return;
            }

            for (int i = _many.Count - 1; i >= 0; i--)
            {
                if (_many[i].Position == position) _many.RemoveAt(i);
            }

            _count = _many.Count;
        }

        public void Clear()
        {
            _many = null;
            _count = 0;
        }

        public readonly int FirstAtOrAfter(int position)
        {
            int low = 0, high = _count;
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (this[mid].Position < position) low = mid + 1;
                else high = mid;
            }

            return low;
        }
    }

    // The slots a footprint's storage effects or reported writes write, with where each write is in them.
    private struct WrittenSlots(TransactionFootprint? footprint, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> executed)
    {
        private int _index = -1;

        public (StorageCell Cell, int Index) Current { get; private set; }

        public readonly WrittenSlots GetEnumerator() => this;

        public bool MoveNext()
        {
            if (footprint is not null)
            {
                ReadOnlySpan<StateEffect> effects = footprint.Effects;
                while (++_index < effects.Length)
                {
                    ref readonly StateEffect effect = ref effects[_index];
                    if (effect.Kind != EffectKind.SetStorage) continue;
                    Current = (effect.Cell, _index);
                    return true;
                }

                return false;
            }

            if (++_index >= executed.Length) return false;
            Current = (executed.Span[_index].Cell, _index);
            return true;
        }
    }
}

/// <summary>What a transaction changes of an account, by kind.</summary>
[Flags]
internal enum AccountChangeKind : byte
{
    None = 0,
    Credit = 1,
    Debit = 2,
    Nonce = 4,
    Code = 8,
    Create = 16,
    Delete = 32,
    // Credited, creating the account if it did not exist.
    MayCreate = 64,
    // Block processing reported the account as it left it.
    Absolute = 128
}

/// <summary>One transaction's change to one account: its kind, and the net balance change and nonce steps it makes.</summary>
internal readonly record struct AccountChange(Address Address, AccountChangeKind Kind, UInt256 Net, ulong NonceSteps)
{
    /// <summary>The account changes of a footprint's effects, one per account.</summary>
    public static AccountChange[] Of(TransactionFootprint footprint)
    {
        ReadOnlySpan<StateEffect> effects = footprint.Effects;
        AccountChange[]? changes = null;
        int count = 0;
        foreach (ref readonly StateEffect effect in effects)
        {
            AccountChangeKind kind = effect.Kind switch
            {
                EffectKind.AddToBalance => effect.Value.IsZero ? AccountChangeKind.None : AccountChangeKind.Credit,
                EffectKind.AddToBalanceAndCreateIfNotExists => AccountChangeKind.MayCreate | (effect.Value.IsZero ? AccountChangeKind.None : AccountChangeKind.Credit),
                EffectKind.SubtractFromBalance => effect.Value.IsZero ? AccountChangeKind.None : AccountChangeKind.Debit,
                EffectKind.IncrementNonce or EffectKind.DecrementNonce or EffectKind.SetNonce => AccountChangeKind.Nonce,
                EffectKind.CreateAccount or EffectKind.CreateAccountIfNotExists => AccountChangeKind.Create,
                EffectKind.DeleteAccount => AccountChangeKind.Delete,
                EffectKind.InsertCode or EffectKind.AdoptCode => AccountChangeKind.Code,
                _ => AccountChangeKind.None
            };
            if (kind == AccountChangeKind.None) continue;

            changes ??= new AccountChange[Math.Min(effects.Length, 8)];
            int at = 0;
            while (at < count && changes[at].Address != effect.Address) at++;
            if (at == count)
            {
                if (count == changes.Length) Array.Resize(ref changes, count * 2);
                changes[count++] = new AccountChange(effect.Address, AccountChangeKind.None, default, 0);
            }

            AccountChange change = changes[at];
            UInt256 net = change.Net;
            ulong steps = change.NonceSteps;
            // The net change wraps for a debit, so two changes compare equal only when they move the balance alike.
            if (effect.Kind is EffectKind.AddToBalance or EffectKind.AddToBalanceAndCreateIfNotExists) UInt256.Add(in net, in effect.Value, out net);
            else if (effect.Kind == EffectKind.SubtractFromBalance) UInt256.Subtract(in net, in effect.Value, out net);
            else if (effect.Kind == EffectKind.IncrementNonce) steps += effect.Nonce;
            changes[at] = change with { Kind = change.Kind | kind, Net = net, NonceSteps = steps };
        }

        return changes is null ? [] : count == changes.Length ? changes : changes[..count];
    }
}

/// <summary>An account as block processing left it after executing a transaction.</summary>
internal readonly record struct ExecutedAccount(Address Address, bool Exists, ulong Nonce, UInt256 Balance);

