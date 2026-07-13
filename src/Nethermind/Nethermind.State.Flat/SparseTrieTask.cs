// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

internal readonly record struct SparseTrieStorageDelta(
    Address Address,
    Hash256 ParentStorageRoot,
    UInt256 Slot,
    byte[] Value);

internal readonly record struct SparseTrieAccountDelta(Address Address, Account? Account);

/// <summary>
/// One execution-phase message. Ownership of both lists and their elements transfers to the
/// worker when the message is enqueued.
/// </summary>
internal sealed record SparseTriePhaseDelta(
    IReadOnlyList<SparseTrieAccountDelta> Accounts,
    IReadOnlyList<SparseTrieStorageDelta> StorageDeltas);

internal readonly record struct SparseTrieFinalSlot(UInt256 Slot, byte[] Value, bool Changed = true);

internal sealed record SparseTrieFinalStorageBatch(
    Address Address,
    Hash256 ParentStorageRoot,
    bool Clear,
    IReadOnlyList<SparseTrieFinalSlot> Slots);

internal readonly record struct SparseTrieFinalAccount(Address Address, Account? Account);

/// <summary>
/// Authoritative final block state. The worker reads but never mutates these collections or their
/// values, allowing the caller to retain the same data for a Patricia fallback.
/// </summary>
internal sealed record SparseTrieFinalState(
    IReadOnlyList<SparseTrieFinalStorageBatch> StorageBatches,
    IReadOnlyList<SparseTrieFinalAccount> Accounts);

internal sealed record SparseTrieBlockResult(
    Hash256 StateRoot,
    IReadOnlyDictionary<AddressAsKey, Hash256> StorageRoots);

/// <summary>
/// Persistent single-owner sparse trie worker. Sequential block sessions reuse the trie only when
/// the next parent root exactly matches the accepted anchor.
/// </summary>
public sealed class SparseTrieWorker : IDisposable, IAsyncDisposable
{
    private const int MaxCommandsPerPass = 8;
    private const int MaxSpeculativeTouchesPerPass = 64;

    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Task _workerTask;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly long _maxRetainedNodes;
    private readonly Dictionary<Hash256, int> _storageArenaHighWater = [];

    private SparseStateTrie _trie = new();
    private long _storageArenaNodes;
    private Hash256? _anchorStateRoot;
    private WorkerSession? _activeSession;
    private bool _stopRequested;
    private bool _disposed;
    private bool _resourcesDisposed;

    /// <summary>
    /// Creates a worker whose background owner stops when <paramref name="cancellationToken"/>
    /// is cancelled or the worker is disposed.
    /// </summary>
    public SparseTrieWorker(
        ILogger logger,
        CancellationToken cancellationToken,
        long maxRetainedNodes = 4_000_000)
    {
        _logger = logger;
        _maxRetainedNodes = Math.Max(0, maxRetainedNodes);
        _workerTask = Task.Factory.StartNew(
            WorkerLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _cancellationRegistration = cancellationToken.Register(
            static state => ((SparseTrieWorker)state!).RequestStop(),
            this);
    }

    internal SparseTrieBlockHandle BeginBlock(Hash256 parentStateRoot, SnapshotBundle bundle) =>
        BeginBlock(parentStateRoot, new ParentStateTrieNodeReader(bundle), bundle);

    internal SparseTrieBlockHandle BeginBlock(Hash256 parentStateRoot, ITrieNodeReader reader) =>
        BeginBlock(parentStateRoot, reader, bundle: null);

    private SparseTrieBlockHandle BeginBlock(
        Hash256 parentStateRoot,
        ITrieNodeReader reader,
        SnapshotBundle? bundle)
    {
        WorkerSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopRequested, this);
            if (_activeSession is not null)
                throw new InvalidOperationException("A sparse trie block session is already active.");

            session = new WorkerSession(parentStateRoot, reader, bundle);
            _activeSession = session;
        }

        if (!_commands.Writer.TryWrite(new BeginCommand(session)))
        {
            session.Poison(new ObjectDisposedException(nameof(SparseTrieWorker)));
            ReleaseSession(session);
            throw new ObjectDisposedException(nameof(SparseTrieWorker));
        }

        return new SparseTrieBlockHandle(this, session);
    }

    internal void EnqueueDelta(WorkerSession session, SparseTriePhaseDelta delta)
    {
        if (!_commands.Writer.TryWrite(new DeltaCommand(session, delta)))
        {
            ObjectDisposedException exception = new(nameof(SparseTrieWorker));
            session.Poison(exception);
            throw exception;
        }
    }

    internal bool TryEnqueueAccountTouch(WorkerSession session, ValueHash256 accountPath)
    {
        if (!TryAddPendingAccountTouch(session, accountPath))
            return true;

        return TryQueueAccountTouches(session);
    }

    internal bool TryEnqueueAccountTouch(
        WorkerSession session,
        ValueHash256 accountPath,
        IReadOnlyList<WarmedTrieNode> proof)
    {
        bool proofAdded = session.HintedAccountProofs.TryAdd(accountPath, 0);
        if (proofAdded)
            session.PendingAccountProofs.Enqueue(new AccountProof(accountPath, proof));
        session.HintedAccounts.TryAdd(accountPath, 0);
        if (!proofAdded)
            return true;

        return TryQueueAccountTouches(session);
    }

    private bool TryQueueAccountTouches(WorkerSession session)
    {
        if (Interlocked.CompareExchange(ref session.AccountTouchCommandQueued, 1, 0) != 0)
            return true;

        if (_commands.Writer.TryWrite(new AccountTouchesCommand(session)))
            return true;

        session.Poison(new ObjectDisposedException(nameof(SparseTrieWorker)));
        return false;
    }

    private static bool TryAddPendingAccountTouch(
        WorkerSession session,
        ValueHash256 accountPath)
    {
        if (!session.HintedAccounts.TryAdd(accountPath, 0))
            return false;

        session.PendingAccountTouches.Enqueue(accountPath);
        return true;
    }

    internal bool TryEnqueueStorageTouch(
        WorkerSession session,
        Hash256 accountHash,
        Hash256 parentStorageRoot,
        ValueHash256 slotPath)
    {
        if (!session.HintedStorage.TryAdd((accountHash, slotPath), 0))
            return true;

        session.PendingStorageTouches.Enqueue(new StorageTouch(accountHash, parentStorageRoot, slotPath));
        return TryQueueStorageTouches(session);
    }

    internal bool TryEnqueueStorageTouch(
        WorkerSession session,
        Hash256 accountHash,
        Hash256 parentStorageRoot,
        ValueHash256 slotPath,
        IReadOnlyList<WarmedTrieNode> proof)
    {
        (Hash256 AccountHash, ValueHash256 SlotPath) key = (accountHash, slotPath);
        bool proofAdded = session.HintedStorageProofs.TryAdd(key, 0);
        if (proofAdded)
        {
            session.PendingStorageProofs.Enqueue(
                new StorageProof(accountHash, parentStorageRoot, slotPath, proof));
        }

        session.HintedStorage.TryAdd(key, 0);
        if (!proofAdded)
            return true;

        return TryQueueStorageTouches(session);
    }

    private bool TryQueueStorageTouches(WorkerSession session)
    {
        if (Interlocked.CompareExchange(ref session.StorageTouchCommandQueued, 1, 0) != 0)
            return true;

        if (_commands.Writer.TryWrite(new StorageTouchesCommand(session)))
            return true;

        session.Poison(new ObjectDisposedException(nameof(SparseTrieWorker)));
        return false;
    }

    internal Task<SparseTrieBlockResult> FinishAsync(
        WorkerSession session,
        SparseTrieFinalState finalState)
    {
        TaskCompletionSource<SparseTrieBlockResult> completion = NewCompletion<SparseTrieBlockResult>();
        session.FinishCompletion = completion;
        if (!_commands.Writer.TryWrite(new FinishCommand(session, finalState, completion)))
        {
            ObjectDisposedException exception = new(nameof(SparseTrieWorker));
            session.Poison(exception);
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    internal Task PrepareCommitAsync(WorkerSession session) =>
        QueueCompletion(session, static (worker, active) => worker.PrepareCommit(active));

    internal Task AcceptAsync(WorkerSession session) =>
        QueueCompletion(session, static (worker, active) => worker.Accept(active));

    internal Task RejectAsync(WorkerSession session) =>
        QueueCompletion(session, static (worker, active) => worker.Reject(active));

    internal Task AbortAsync(WorkerSession session) =>
        QueueCompletion(session, static (worker, active) => worker.Abort(active));

    private Task QueueCompletion(
        WorkerSession session,
        Action<SparseTrieWorker, WorkerSession> action)
    {
        TaskCompletionSource completion = NewCompletion();
        if (!_commands.Writer.TryWrite(new CompletionCommand(session, action, completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(SparseTrieWorker)));
        return completion.Task;
    }

    private void WorkerLoop()
    {
        try
        {
            while (_commands.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                bool continueProcessing;
                do
                {
                    int commandsProcessed = 0;
                    while (commandsProcessed < MaxCommandsPerPass &&
                           _commands.Reader.TryRead(out Command? command))
                    {
                        if (command is StopCommand stop)
                        {
                            StopActiveSession();
                            stop.Completion?.TrySetResult();
                            return;
                        }

                        ProcessCommand(command);
                        commandsProcessed++;
                    }

                    continueProcessing = TryProcessSpeculativeTouches();
                } while (continueProcessing || _commands.Reader.TryPeek(out _));
            }
        }
        catch (Exception exception)
        {
            _activeSession?.Poison(exception);
            if (_logger.IsError)
                _logger.Error("Sparse trie worker terminated unexpectedly.", exception);
        }
        finally
        {
            StopActiveSession();
            _commands.Writer.TryComplete();
        }
    }

    private void ProcessCommand(Command command)
    {
        try
        {
            switch (command)
            {
                case BeginCommand begin:
                    Begin(begin.Session!);
                    break;
                case DeltaCommand delta:
                    EnsureActive(delta.Session!);
                    if (delta.Session!.Error is null)
                        ProcessDelta(delta.Session!, delta.Delta);
                    break;
                case AccountTouchesCommand touches:
                    {
                        WorkerSession session = touches.Session!;
                        Volatile.Write(ref session.AccountTouchCommandQueued, 0);
                        if (!session.FinishRequested && session.Error is null)
                        {
                            int remaining = MaxSpeculativeTouchesPerPass;
                            ProcessAccountProofs(session, ref remaining);
                            ProcessAccountTouches(session, ref remaining);
                        }
                        break;
                    }
                case StorageTouchesCommand touches:
                    {
                        WorkerSession session = touches.Session!;
                        Volatile.Write(ref session.StorageTouchCommandQueued, 0);
                        if (!session.FinishRequested && session.Error is null)
                        {
                            int remaining = MaxSpeculativeTouchesPerPass;
                            ProcessStorageProofs(session, ref remaining);
                            ProcessStorageTouches(session, ref remaining);
                        }
                        break;
                    }
                case FinishCommand finish:
                    Finish(finish);
                    break;
                case CompletionCommand completion:
                    EnsureActive(completion.Session!);
                    completion.Action(this, completion.Session!);
                    completion.Completion.TrySetResult();
                    break;
            }
        }
        catch (Exception exception)
        {
            command.Session?.Poison(exception);
            switch (command)
            {
                case FinishCommand finish:
                    finish.Completion.TrySetException(exception);
                    break;
                case CompletionCommand completion:
                    completion.Completion.TrySetException(exception);
                    break;
            }

            if (_logger.IsWarn)
                _logger.Warn($"Sparse trie worker session faulted: {exception.Message}");
        }
    }

    private void Begin(WorkerSession session)
    {
        EnsureActive(session);
        if (_anchorStateRoot != session.ParentStateRoot)
        {
            ClearTrie();
        }

        session.Computer = new SparseRootComputer(_trie, session.Reader, session.ParentStateRoot);
    }

    private void ProcessDelta(WorkerSession session, SparseTriePhaseDelta delta)
    {
        SparseRootComputer computer = session.GetComputer();
        Dictionary<ValueHash256, LeafUpdate> accountUpdates = [];

        foreach (SparseTrieAccountDelta accountDelta in delta.Accounts)
        {
            ValueHash256 accountPath = accountDelta.Address.ToAccountPath;
            session.RevealedAccounts.Add(accountPath);
            session.ProvisionalAccountValues[accountDelta.Address] = accountDelta.Account;
            accountUpdates[accountPath] = accountDelta.Account is null
                ? LeafUpdate.Deleted()
                : LeafUpdate.Changed(AccountDecoder.Instance.Encode(accountDelta.Account).Bytes);
        }

        foreach (SparseTrieStorageDelta storageDelta in delta.StorageDeltas)
        {
            ValueHash256 accountPath = storageDelta.Address.ToAccountPath;
            Hash256 accountHash = accountPath.ToCommitment();
            session.RetainedStorageHashes.Add(accountHash);
            AddAccountTouch(session, accountUpdates, accountPath);

            ref ProvisionalStorage? provisional = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                session.ProvisionalStorage,
                accountHash,
                out bool exists);
            if (!exists)
            {
                provisional = new ProvisionalStorage(storageDelta.Address, storageDelta.ParentStorageRoot);
            }
            else if (provisional!.ParentStorageRoot != storageDelta.ParentStorageRoot)
            {
                throw new InvalidOperationException(
                    $"Conflicting parent storage roots for {storageDelta.Address}.");
            }

            ValueHash256 slotPath = default;
            StorageTree.ComputeKeyWithLookup(storageDelta.Slot, ref slotPath);
            if (!provisional!.TouchedSlots.Add(slotPath))
                continue;

            TryEnqueueStorageTouch(
                session,
                accountHash,
                provisional.ParentStorageRoot,
                slotPath);
        }

        computer.ApplyAccountChanges(accountUpdates);
    }

    private static void AddAccountTouch(
        WorkerSession session,
        Dictionary<ValueHash256, LeafUpdate> touches,
        ValueHash256 accountPath)
    {
        if (session.RevealedAccounts.Add(accountPath))
            touches.Add(accountPath, LeafUpdate.Touched());
    }

    private bool TryProcessSpeculativeTouches()
    {
        WorkerSession? session = _activeSession;
        if (session is null || session.FinishRequested || session.Error is not null)
            return false;
        if (session.PendingStorageProofs.IsEmpty && session.PendingStorageTouches.IsEmpty &&
            session.PendingAccountProofs.IsEmpty && session.PendingAccountTouches.IsEmpty)
            return false;

        try
        {
            int remaining = MaxSpeculativeTouchesPerPass;
            if (!session.PendingStorageProofs.IsEmpty)
                ProcessStorageProofs(session, ref remaining);
            if (!session.PendingStorageTouches.IsEmpty)
                ProcessStorageTouches(session, ref remaining);
            if (remaining != 0 && !session.PendingAccountProofs.IsEmpty)
                ProcessAccountProofs(session, ref remaining);
            if (remaining != 0 && !session.PendingAccountTouches.IsEmpty)
                ProcessAccountTouches(session, ref remaining);

            return !session.PendingStorageProofs.IsEmpty || !session.PendingStorageTouches.IsEmpty ||
                !session.PendingAccountProofs.IsEmpty || !session.PendingAccountTouches.IsEmpty;
        }
        catch (Exception exception)
        {
            session.Poison(exception);
            DiscardSpeculativeTouches(session);
            if (_logger.IsWarn)
                _logger.Warn($"Sparse trie worker session faulted: {exception.Message}");
            return false;
        }
    }

    private static void ProcessAccountProofs(WorkerSession session, ref int remaining)
    {
        SparseRootComputer computer = session.GetComputer();
        while (remaining > 0 && session.PendingAccountProofs.TryDequeue(out AccountProof proof))
        {
            remaining--;
            computer.RevealAccountProof(proof.AccountPath, proof.Nodes);
        }
    }

    private static void ProcessStorageProofs(WorkerSession session, ref int remaining)
    {
        SparseRootComputer computer = session.GetComputer();
        while (remaining > 0 && session.PendingStorageProofs.TryDequeue(out StorageProof proof))
        {
            remaining--;
            computer.RevealStorageProof(
                proof.AccountHash,
                proof.ParentStorageRoot,
                proof.SlotPath,
                proof.Nodes);
        }
    }

    private static void ProcessAccountTouches(WorkerSession session, ref int remaining)
    {
        Dictionary<ValueHash256, LeafUpdate> updates = [];
        while (remaining > 0 &&
               session.PendingAccountTouches.TryDequeue(out ValueHash256 accountPath))
        {
            remaining--;
            AddAccountTouch(session, updates, accountPath);
        }

        if (updates.Count != 0)
            session.GetComputer().ApplyAccountChanges(updates);
    }

    private static void ProcessStorageTouches(WorkerSession session, ref int remaining)
    {
        Dictionary<Hash256, StorageTouchGroup> groups = [];
        while (remaining > 0 &&
               session.PendingStorageTouches.TryDequeue(out StorageTouch touch))
        {
            remaining--;
            if (!groups.TryGetValue(touch.AccountHash, out StorageTouchGroup? group))
            {
                group = new StorageTouchGroup(touch.ParentStorageRoot);
                groups.Add(touch.AccountHash, group);
            }
            else if (group.ParentStorageRoot != touch.ParentStorageRoot)
            {
                throw new InvalidOperationException(
                    $"Conflicting hinted storage roots for account {touch.AccountHash}.");
            }

            group.Updates[touch.SlotPath] = LeafUpdate.Touched();
            session.RetainedStorageHashes.Add(touch.AccountHash);
        }

        SparseRootComputer computer = session.GetComputer();
        foreach (KeyValuePair<Hash256, StorageTouchGroup> entry in groups)
            computer.ApplyStorageChanges(entry.Key, entry.Value.ParentStorageRoot, entry.Value.Updates);
    }

    private void Finish(FinishCommand command)
    {
        WorkerSession session = command.Session!;
        EnsureActive(session);
        if (session.FinishRequested)
            throw new InvalidOperationException("Sparse trie block session was already finalized.");
        session.FinishRequested = true;
        try
        {
            if (session.Error is null)
                ProcessPendingProofs(session);
        }
        finally
        {
            DiscardSpeculativeTouches(session);
        }

        if (session.Error is not null)
        {
            command.Completion.TrySetException(session.Error);
            return;
        }

        SparseTrieBlockResult result = ComputeFinalResult(session, command.FinalState);
        session.Result = result;
        SparseRootComputer computer = session.GetComputer();
        Metrics.SparseProofNodesRead += computer.LastProofNodeCount;
        Metrics.SparseProofRetries += computer.LastRetryCount;
        command.Completion.TrySetResult(result);
    }

    private static void ProcessPendingProofs(WorkerSession session)
    {
        int remaining = int.MaxValue;
        ProcessStorageProofs(session, ref remaining);
        ProcessAccountProofs(session, ref remaining);
    }

    private static void DiscardSpeculativeTouches(WorkerSession session)
    {
        session.PendingAccountProofs.Clear();
        session.PendingAccountTouches.Clear();
        session.PendingStorageProofs.Clear();
        session.PendingStorageTouches.Clear();
        Volatile.Write(ref session.AccountTouchCommandQueued, 0);
        Volatile.Write(ref session.StorageTouchCommandQueued, 0);
    }

    private SparseTrieBlockResult ComputeFinalResult(
        WorkerSession session,
        SparseTrieFinalState finalState)
    {
        SparseRootComputer computer = session.GetComputer();
        Dictionary<AddressAsKey, Account?> authoritativeAccounts = new(
            finalState.Accounts.Count,
            AddressAsKey.EqualityComparer);
        foreach (SparseTrieFinalAccount finalAccount in finalState.Accounts)
        {
            if (!authoritativeAccounts.TryAdd(finalAccount.Address, finalAccount.Account))
                throw new InvalidOperationException($"Duplicate final account for {finalAccount.Address}.");
        }

        int storageCount = finalState.StorageBatches.Count;
        StorageRootJob[] storageJobs = new StorageRootJob[storageCount];
        HashSet<Hash256> finalizedStorage = [];

        for (int i = 0; i < storageCount; i++)
        {
            SparseTrieFinalStorageBatch batch = finalState.StorageBatches[i];
            ValueHash256 accountPath = batch.Address.ToAccountPath;
            Hash256 accountHash = accountPath.ToCommitment();
            if (!finalizedStorage.Add(accountHash))
                throw new InvalidOperationException($"Duplicate final storage batch for {batch.Address}.");
            if (!authoritativeAccounts.TryGetValue(batch.Address, out Account? storageOwner))
                throw new InvalidOperationException($"Final accounts omit storage owner {batch.Address}.");
            if (storageOwner is null)
            {
                session.DirtyStorageHashes.Add(accountHash);
                session.RetainedStorageHashes.Add(accountHash);
                storageJobs[i] = new StorageRootJob(
                    batch.Address,
                    accountHash,
                    Keccak.EmptyTreeHash,
                    ParentRoot: Keccak.EmptyTreeHash,
                    Updates: null,
                    Wipe: true);
                continue;
            }

            session.DirtyStorageHashes.Add(accountHash);
            session.RetainedStorageHashes.Add(accountHash);

            if (session.ProvisionalStorage.TryGetValue(accountHash, out ProvisionalStorage? provisional) &&
                provisional.ParentStorageRoot != batch.ParentStorageRoot)
            {
                throw new InvalidOperationException(
                    $"Final parent storage root differs from provisional root for {batch.Address}.");
            }

            Dictionary<ValueHash256, LeafUpdate> updates = new(batch.Slots.Count);
            HashSet<ValueHash256> finalSlots = new(batch.Slots.Count);
            foreach (SparseTrieFinalSlot slot in batch.Slots)
            {
                ValueHash256 slotPath = default;
                StorageTree.ComputeKeyWithLookup(slot.Slot, ref slotPath);
                if (!finalSlots.Add(slotPath))
                    throw new InvalidOperationException($"Duplicate final storage slot for {batch.Address}.");
                if (batch.Clear || slot.Changed)
                    updates[slotPath] = EncodeStorageValue(slot.Value);
            }

            if (!batch.Clear && provisional is not null &&
                !finalSlots.IsSupersetOf(provisional.TouchedSlots))
            {
                throw new InvalidOperationException(
                    $"Final storage batch for {batch.Address} omits a provisionally changed slot.");
            }

            Hash256 effectiveParentRoot = batch.ParentStorageRoot;
            if (batch.Clear)
                effectiveParentRoot = Keccak.EmptyTreeHash;

            storageJobs[i] = new StorageRootJob(
                batch.Address,
                accountHash,
                updates.Count == 0 ? effectiveParentRoot : null,
                effectiveParentRoot,
                updates,
                Wipe: batch.Clear);
        }

        foreach (KeyValuePair<Hash256, ProvisionalStorage> provisional in session.ProvisionalStorage)
        {
            if (!finalizedStorage.Contains(provisional.Key))
            {
                if (authoritativeAccounts.TryGetValue(provisional.Value.Address, out Account? account) && account is null)
                {
                    computer.WipeStorage(provisional.Key);
                    session.DirtyStorageHashes.Add(provisional.Key);
                    session.RetainedStorageHashes.Add(provisional.Key);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Final state omits provisionally changed storage for {provisional.Value.Address}.");
                }
            }
        }

        Hash256[] storageRoots = new Hash256[storageCount];
        if (storageCount < 3 || session.Bundle is null)
        {
            for (int i = 0; i < storageCount; i++)
                storageRoots[i] = ApplyAndComputeStorageRoot(computer, storageJobs[i]);
        }
        else
        {
            Parallel.For(
                0,
                storageCount,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                i => storageRoots[i] = ApplyAndComputeStorageRoot(computer, storageJobs[i]));
        }

        Dictionary<AddressAsKey, Hash256> mutableStorageRoots = new(
            storageCount,
            AddressAsKey.EqualityComparer);
        for (int i = 0; i < storageCount; i++)
            mutableStorageRoots.Add(storageJobs[i].Address, storageRoots[i]);

        Dictionary<ValueHash256, LeafUpdate> accountUpdates = new(finalState.Accounts.Count);
        foreach (SparseTrieFinalAccount finalAccount in finalState.Accounts)
        {
            AddressAsKey address = finalAccount.Address;
            ValueHash256 accountPath = finalAccount.Address.ToAccountPath;
            Account? account = finalAccount.Account;
            if (account is null)
            {
                Hash256 accountHash = accountPath.ToCommitment();
                computer.WipeStorage(accountHash);
                session.DirtyStorageHashes.Add(accountHash);
                session.RetainedStorageHashes.Add(accountHash);
            }
            if (account is not null &&
                mutableStorageRoots.TryGetValue(address, out Hash256? storageRoot) &&
                storageRoot is not null)
                account = account.WithChangedStorageRoot(storageRoot);

            if (!session.ProvisionalAccountValues.TryGetValue(address, out Account? provisional) ||
                provisional != account)
            {
                accountUpdates[accountPath] = account is null
                    ? LeafUpdate.Deleted()
                    : LeafUpdate.Changed(AccountDecoder.Instance.Encode(account).Bytes);
            }
        }

        foreach (AddressAsKey storageAddress in mutableStorageRoots.Keys)
        {
            if (!authoritativeAccounts.ContainsKey(storageAddress))
                throw new InvalidOperationException($"Final accounts omit storage owner {storageAddress}.");
        }

        foreach (AddressAsKey provisionalAddress in session.ProvisionalAccountValues.Keys)
        {
            if (!authoritativeAccounts.ContainsKey(provisionalAddress))
                throw new InvalidOperationException(
                    $"Final accounts omit provisionally updated account {provisionalAddress}.");
        }

        Hash256 stateRoot;
        if (accountUpdates.Count == 0 && session.ProvisionalAccountValues.Count == 0)
        {
            if (storageCount != 0)
                throw new InvalidOperationException("Storage changed without a final account leaf.");
            stateRoot = session.ParentStateRoot;
        }
        else
        {
            if (accountUpdates.Count != 0)
            {
                computer.SetAccountChanges(accountUpdates);
                computer.ApplyAccountChanges(accountUpdates);
            }
            stateRoot = computer.ComputeAppliedStateRoot();
        }

        return new SparseTrieBlockResult(
            stateRoot,
            new ReadOnlyDictionary<AddressAsKey, Hash256>(mutableStorageRoots));
    }

    private static Hash256 ApplyAndComputeStorageRoot(SparseRootComputer computer, StorageRootJob job)
    {
        if (job.Wipe)
            computer.WipeStorage(job.AccountHash);

        if (job.Updates is not null)
        {
            if (job.Updates.Count > 0)
                computer.ApplyStorageChanges(job.AccountHash, job.ParentRoot, job.Updates);
            else
                computer.AddStorageChanges(job.AccountHash, job.ParentRoot, job.Updates);
        }

        return job.KnownRoot ?? computer.ComputeAppliedStorageRoot(job.AccountHash, allowParallel: false);
    }

    private static LeafUpdate EncodeStorageValue(byte[] value)
    {
        if (value.IsZero())
            return LeafUpdate.Deleted();

        byte[] encoded = Rlp.Encode(value).Bytes;
        return encoded.Length == 0 ? LeafUpdate.Deleted() : LeafUpdate.Changed(encoded);
    }

    private void PrepareCommit(WorkerSession session)
    {
        if (session.Result is null)
            throw new InvalidOperationException("Finish the sparse trie block before preparing its commit.");
        if (session.Prepared)
            return;
        if (session.Bundle is null)
        {
            session.Prepared = true;
            return;
        }

        SparseTrieSnapshotCommitter.CommitAccountTrie(_trie.AccountTrie.Subtrie, session.Bundle);
        if (session.DirtyStorageHashes.Count < 3)
        {
            foreach (Hash256 accountHash in session.DirtyStorageHashes)
                CommitStorageTrie(session, accountHash);
        }
        else
        {
            Parallel.ForEach(
                session.DirtyStorageHashes,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                accountHash => CommitStorageTrie(session, accountHash));
        }
        session.Prepared = true;
    }

    private void CommitStorageTrie(WorkerSession session, Hash256 accountHash)
    {
        if (_trie.StorageTries.TryGetValue(accountHash, out SparsePatriciaTree? storageTrie))
        {
            SparseTrieSnapshotCommitter.CommitStorageTrie(
                storageTrie.Subtrie,
                session.Bundle!,
                accountHash);
        }
    }

    private void Accept(WorkerSession session)
    {
        if (session.Result is null)
            throw new InvalidOperationException("Cannot accept a sparse trie block without a result.");
        if (!session.Prepared)
            throw new InvalidOperationException("Prepare the sparse trie snapshot commit before accepting the block.");

        _anchorStateRoot = session.Result.StateRoot;
        UpdateRetainedSize(session);
        if (Metrics.SparseAccountArenaNodes + _storageArenaNodes > _maxRetainedNodes)
        {
            if (_logger.IsInfo)
            {
                _logger.Info(
                    $"Sparse trie retained-node limit {_maxRetainedNodes} exceeded; " +
                    "the next block will rebuild from persisted proofs.");
            }
            Metrics.SparseCacheResets++;
            ClearTrie();
        }
        CompleteSession(session);
    }

    private void Reject(WorkerSession session)
    {
        if (session.Prepared)
            throw new InvalidOperationException("Cannot reject after sparse trie nodes were prepared for commit.");
        ClearAndCompleteSession(session);
    }

    private void Abort(WorkerSession session) => ClearAndCompleteSession(session);

    private void ClearAndCompleteSession(WorkerSession session)
    {
        ClearTrie();
        CompleteSession(session);
    }

    private void UpdateRetainedSize(WorkerSession session)
    {
        foreach (Hash256 accountHash in session.RetainedStorageHashes)
        {
            _storageArenaHighWater.TryGetValue(accountHash, out int previous);
            int current = _trie.StorageTries.TryGetValue(accountHash, out SparsePatriciaTree? storageTrie)
                ? storageTrie.ArenaHighWater
                : 0;
            _storageArenaNodes += current - previous;
            if (current == 0)
                _storageArenaHighWater.Remove(accountHash);
            else
                _storageArenaHighWater[accountHash] = current;
        }

        Metrics.SparseRetainedStorageTries = _trie.StorageTries.Count;
        Metrics.SparseAccountArenaNodes = _trie.IsRevealed ? _trie.AccountTrie.ArenaHighWater : 0;
        Metrics.SparseStorageArenaNodes = _storageArenaNodes;
    }

    private void ClearTrie()
    {
        _trie.Clear();
        _anchorStateRoot = null;
        _storageArenaHighWater.Clear();
        _storageArenaNodes = 0;
        Metrics.SparseRetainedStorageTries = 0;
        Metrics.SparseAccountArenaNodes = 0;
        Metrics.SparseStorageArenaNodes = 0;
    }

    private void CompleteSession(WorkerSession session)
    {
        EnsureActive(session);
        session.Completed = true;
        ReleaseSession(session);
    }

    private void EnsureActive(WorkerSession session)
    {
        if (!ReferenceEquals(_activeSession, session))
            throw new InvalidOperationException("Sparse trie command belongs to an inactive block session.");
    }

    private void ReleaseSession(WorkerSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeSession, session))
                _activeSession = null;
        }
    }

    private void StopActiveSession()
    {
        WorkerSession? session = _activeSession;
        if (session is null)
            return;

        ObjectDisposedException exception = new(nameof(SparseTrieWorker));
        session.Poison(exception);
        ClearTrie();
        ReleaseSession(session);
    }

    private void RequestStop()
    {
        lock (_gate)
        {
            if (_stopRequested)
                return;
            _stopRequested = true;
        }

        _commands.Writer.TryWrite(new StopCommand(Completion: null));
        _commands.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
        }

        Task stopTask = StopAsync();
        stopTask.GetAwaiter().GetResult();
        _workerTask.GetAwaiter().GetResult();
        _cancellationRegistration.Dispose();
        _trie.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
        }

        await StopAsync();
        await _workerTask;
        _cancellationRegistration.Dispose();
        _trie.Dispose();
    }

    private Task StopAsync()
    {
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_disposed)
                return _workerTask;
            _disposed = true;

            if (!_stopRequested)
            {
                _stopRequested = true;
                completion = NewCompletion();
            }
        }

        if (completion is null)
            return _workerTask;

        if (!_commands.Writer.TryWrite(new StopCommand(completion)))
            completion.TrySetResult();
        _commands.Writer.TryComplete();
        return completion.Task;
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class WorkerSession(
        Hash256 parentStateRoot,
        ITrieNodeReader reader,
        SnapshotBundle? bundle)
    {
        private Exception? _error;

        public Hash256 ParentStateRoot { get; } = parentStateRoot;
        public ITrieNodeReader Reader { get; } = reader;
        public SnapshotBundle? Bundle { get; } = bundle;
        public SparseRootComputer? Computer { get; set; }
        public Dictionary<Hash256, ProvisionalStorage> ProvisionalStorage { get; } = [];
        public HashSet<ValueHash256> RevealedAccounts { get; } = [];
        public ConcurrentDictionary<ValueHash256, byte> HintedAccounts { get; } = [];
        public ConcurrentDictionary<ValueHash256, byte> HintedAccountProofs { get; } = [];
        public ConcurrentQueue<AccountProof> PendingAccountProofs { get; } = [];
        public ConcurrentQueue<ValueHash256> PendingAccountTouches { get; } = [];
        public ConcurrentDictionary<(Hash256 AccountHash, ValueHash256 SlotPath), byte> HintedStorage { get; } = [];
        public ConcurrentDictionary<(Hash256 AccountHash, ValueHash256 SlotPath), byte> HintedStorageProofs { get; } = [];
        public ConcurrentQueue<StorageProof> PendingStorageProofs { get; } = [];
        public ConcurrentQueue<StorageTouch> PendingStorageTouches { get; } = [];
        public Dictionary<AddressAsKey, Account?> ProvisionalAccountValues { get; } = new(AddressAsKey.EqualityComparer);
        public HashSet<Hash256> DirtyStorageHashes { get; } = [];
        public HashSet<Hash256> RetainedStorageHashes { get; } = [];
        public SparseTrieBlockResult? Result { get; set; }
        public TaskCompletionSource<SparseTrieBlockResult>? FinishCompletion { get; set; }
        public Exception? Error => Volatile.Read(ref _error);
        public bool FinishRequested { get; set; }
        public bool Prepared { get; set; }
        public bool Completed { get; set; }
        public int AccountTouchCommandQueued;
        public int StorageTouchCommandQueued;

        public SparseRootComputer GetComputer() =>
            Computer ?? throw new InvalidOperationException("Sparse trie block has not started.");

        public void Poison(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _error, exception, comparand: null) is null)
                FinishCompletion?.TrySetException(exception);
        }
    }

    internal sealed class ProvisionalStorage(Address address, Hash256 parentStorageRoot)
    {
        public Address Address { get; } = address;
        public Hash256 ParentStorageRoot { get; } = parentStorageRoot;
        public HashSet<ValueHash256> TouchedSlots { get; } = [];
    }

    internal readonly record struct StorageTouch(
        Hash256 AccountHash,
        Hash256 ParentStorageRoot,
        ValueHash256 SlotPath);

    internal readonly record struct AccountProof(
        ValueHash256 AccountPath,
        IReadOnlyList<WarmedTrieNode> Nodes);

    internal readonly record struct StorageProof(
        Hash256 AccountHash,
        Hash256 ParentStorageRoot,
        ValueHash256 SlotPath,
        IReadOnlyList<WarmedTrieNode> Nodes);

    private sealed class StorageTouchGroup(Hash256 parentStorageRoot)
    {
        public Hash256 ParentStorageRoot { get; } = parentStorageRoot;
        public Dictionary<ValueHash256, LeafUpdate> Updates { get; } = [];
    }

    private readonly record struct StorageRootJob(
        AddressAsKey Address,
        Hash256 AccountHash,
        Hash256? KnownRoot,
        Hash256 ParentRoot,
        Dictionary<ValueHash256, LeafUpdate>? Updates,
        bool Wipe);

    private abstract record Command(WorkerSession? Session);
    private sealed record BeginCommand(WorkerSession BlockSession) : Command(BlockSession);
    private sealed record DeltaCommand(WorkerSession BlockSession, SparseTriePhaseDelta Delta) : Command(BlockSession);
    private sealed record AccountTouchesCommand(WorkerSession BlockSession) : Command(BlockSession);
    private sealed record StorageTouchesCommand(WorkerSession BlockSession) : Command(BlockSession);
    private sealed record FinishCommand(
        WorkerSession BlockSession,
        SparseTrieFinalState FinalState,
        TaskCompletionSource<SparseTrieBlockResult> Completion) : Command(BlockSession);
    private sealed record CompletionCommand(
        WorkerSession BlockSession,
        Action<SparseTrieWorker, WorkerSession> Action,
        TaskCompletionSource Completion) : Command(BlockSession);
    private sealed record StopCommand(TaskCompletionSource? Completion) : Command(Session: null);
}

/// <summary>
/// Producer-side handle for one sequential block session.
/// </summary>
internal sealed class SparseTrieBlockHandle : IDisposable, IAsyncDisposable
{
    private readonly SparseTrieWorker _worker;
    private readonly SparseTrieWorker.WorkerSession _session;
    private readonly object _gate = new();
    private Task<SparseTrieBlockResult>? _finishTask;
    private Task? _completionTask;
    private bool _terminal;

    internal SparseTrieBlockHandle(
        SparseTrieWorker worker,
        SparseTrieWorker.WorkerSession session)
    {
        _worker = worker;
        _session = session;
    }

    public void EnqueueDelta(SparseTriePhaseDelta delta)
    {
        lock (_gate)
        {
            if (_finishTask is not null || _completionTask is not null || _terminal)
                throw new InvalidOperationException("The sparse trie block is already finalizing.");
            _worker.EnqueueDelta(_session, delta);
        }
    }

    public bool TryEnqueueAccountTouch(ValueHash256 accountPath)
    {
        lock (_gate)
        {
            if (_finishTask is not null || _completionTask is not null || _terminal)
                return false;
            return _worker.TryEnqueueAccountTouch(_session, accountPath);
        }
    }

    public bool TryEnqueueAccountTouch(
        ValueHash256 accountPath,
        IReadOnlyList<WarmedTrieNode> proof)
    {
        lock (_gate)
        {
            if (_finishTask is not null || _completionTask is not null || _terminal)
                return false;
            return _worker.TryEnqueueAccountTouch(_session, accountPath, proof);
        }
    }

    public bool TryEnqueueStorageTouch(
        Hash256 accountHash,
        Hash256 parentStorageRoot,
        ValueHash256 slotPath)
    {
        lock (_gate)
        {
            if (_finishTask is not null || _completionTask is not null || _terminal)
                return false;
            return _worker.TryEnqueueStorageTouch(
                _session,
                accountHash,
                parentStorageRoot,
                slotPath);
        }
    }

    public bool TryEnqueueStorageTouch(
        Hash256 accountHash,
        Hash256 parentStorageRoot,
        ValueHash256 slotPath,
        IReadOnlyList<WarmedTrieNode> proof)
    {
        lock (_gate)
        {
            if (_finishTask is not null || _completionTask is not null || _terminal)
                return false;
            return _worker.TryEnqueueStorageTouch(
                _session,
                accountHash,
                parentStorageRoot,
                slotPath,
                proof);
        }
    }

    public Task<SparseTrieBlockResult> FinishAsync(SparseTrieFinalState finalState)
    {
        lock (_gate)
        {
            if (_completionTask is not null || _terminal)
                throw new InvalidOperationException("The sparse trie block is already complete.");
            return _finishTask ??= _worker.FinishAsync(_session, finalState);
        }
    }

    public Task PrepareCommitAsync()
    {
        lock (_gate)
        {
            if (_finishTask is null)
                throw new InvalidOperationException("Finish the sparse trie block before preparing its commit.");
            if (_terminal)
                throw new InvalidOperationException("The sparse trie block is already complete.");
            return _worker.PrepareCommitAsync(_session);
        }
    }

    public Task AcceptAsync() => CompleteAsync(_worker.AcceptAsync);

    public Task RejectAsync() => CompleteAsync(_worker.RejectAsync);

    public Task AbortAsync() => CompleteAsync(_worker.AbortAsync);

    private Task CompleteAsync(
        Func<SparseTrieWorker.WorkerSession, Task> completion)
    {
        lock (_gate)
        {
            if (_terminal)
                return Task.CompletedTask;
            return _completionTask ??= CompleteCoreAsync(completion);
        }
    }

    private async Task CompleteCoreAsync(
        Func<SparseTrieWorker.WorkerSession, Task> completion)
    {
        try
        {
            await Task.Yield();
            await completion(_session);
            lock (_gate)
            {
                _terminal = true;
                _completionTask = Task.CompletedTask;
            }
        }
        catch
        {
            lock (_gate)
                _completionTask = null;
            throw;
        }
    }

    public void Dispose() => AbortAsync().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(AbortAsync());
}
