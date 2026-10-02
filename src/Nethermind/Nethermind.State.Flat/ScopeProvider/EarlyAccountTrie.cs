// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Diagnostics;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// The account values a block's execution commits, applied and hashed into a copy of the block-start state trie while
/// the block executes, so the block-end write batch only sets and hashes what changed since.
/// </summary>
/// <remarks>
/// One per block of a writable main-processing scope. Values come from the block's own commits only. The early trie is
/// adopted by the first write batch of the block, which then sets every dirty account whose final value differs from
/// the applied one, and puts back the pre-block value of any applied account the block left unchanged. A pass still
/// running at that moment makes the batch drop the early trie and set every account as before.
/// </remarks>
internal sealed class EarlyAccountTrie(SnapshotBundle bundle, Hash256 preBlockRoot, ILogManager logManager)
{
    private const int Idle = 0;
    private const int Applying = 1;
    private const int Claimed = 2;

    private readonly SnapshotBundle _bundle = bundle;
    private readonly Hash256 _preBlockRoot = preBlockRoot;
    private readonly ILogManager _logManager = logManager;
    private readonly ConcurrentQueue<(AddressAsKey Address, Account? Account)> _writes = new();
    // Owned by the applier thread until the trie is claimed.
    private readonly Dictionary<AddressAsKey, Account?> _applied = [];
    private StateTree? _tree;
    private int _state = Idle;
    private int _queued;
    private int _sinceHash;

    internal int AppliedCount => _applied.Count;

    /// <summary>Whether every queued write has been applied and no pass is running. For tests.</summary>
    internal bool Drained => _writes.IsEmpty && Volatile.Read(ref _state) != Applying && Volatile.Read(ref _queued) == 0;

    public void Add(Address address, Account? account)
    {
        _writes.Enqueue((address, account));
        if (Volatile.Read(ref _queued) == 0 && Interlocked.Exchange(ref _queued, 1) == 0) Applier.Instance.Enqueue(this);
    }

    /// <summary>One pass on the applier thread: the latest value of every queued account goes into the early trie.</summary>
    internal void Apply()
    {
        Interlocked.Exchange(ref _queued, 0);
        if (Interlocked.CompareExchange(ref _state, Applying, Idle) != Idle) return;

        bool leased = false;
        try
        {
            if (_writes.IsEmpty || !(leased = _bundle.TryLeaseReadOnlyBundle())) return;

            Dictionary<AddressAsKey, Account?> changed = [];
            while (_writes.TryDequeue(out (AddressAsKey Address, Account? Account) write)) changed[write.Address] = write.Account;
            foreach ((AddressAsKey address, Account? account) in changed)
            {
                if (_applied.TryGetValue(address, out Account? current) && current == account) changed.Remove(address);
                else _applied[address] = account;
            }

            if (changed.Count == 0) return;

            StateTree tree = _tree ??= new StateTree(new StateTrieStoreWarmerAdapter(_bundle), _logManager) { RootHash = _preBlockRoot };
            Set(tree, changed);
            _sinceHash += changed.Count;
            // Hash when the feed is quiet, or every so often under a steady feed, so the block end finds most of it done.
            if (_writes.IsEmpty || _sinceHash >= ExperimentKnobs.EarlyAccountHashEvery)
            {
                tree.UpdateRootHash(canBeParallel: false);
                _sinceHash = 0;
            }
        }
        catch
        {
            // A partly applied trie must never be adopted.
            _tree = null;
            _applied.Clear();
            Volatile.Write(ref _state, Claimed);
            throw;
        }
        finally
        {
            if (leased) _bundle.ReleaseReadOnlyBundleLease();
            Interlocked.CompareExchange(ref _state, Idle, Applying);
        }
    }

    /// <summary>
    /// Hands the early trie to the block's state trie and returns the accounts it still has to set; <c>false</c> when
    /// there is nothing to adopt or a pass was running, and the batch sets every account itself.
    /// </summary>
    internal bool TryAdopt(StateTree target, Dictionary<AddressAsKey, Account?> dirty, out Dictionary<AddressAsKey, Account?> remaining)
    {
        remaining = dirty;
        int previous = Interlocked.Exchange(ref _state, Claimed);
        if (previous != Idle || _tree is not { } tree || _applied.Count == 0) return false;

        Dictionary<AddressAsKey, Account?> rest = new(dirty.Count);
        foreach ((AddressAsKey address, Account? account) in dirty)
        {
            if (!_applied.TryGetValue(address, out Account? applied) || applied != account) rest[address] = account;
        }

        foreach ((AddressAsKey address, Account? applied) in _applied)
        {
            if (dirty.ContainsKey(address)) continue;
            // Not dirty at the end of the block: the bundle still holds its pre-block value.
            Account? preBlock = _bundle.GetAccount(address);
            if (applied != preBlock) rest[address] = preBlock;
        }

        target.RootRef = tree.RootRef;
        remaining = rest;
        return true;
    }

    internal static void Set(StateTree tree, Dictionary<AddressAsKey, Account?> accounts)
    {
        if (Avx2.IsSupported && accounts.Count >= KeyHashBatch.MinimumBatchSize)
        {
            tree.SetAccounts(accounts, PatriciaTree.Flags.DoNotParallelize);
            return;
        }

        foreach ((AddressAsKey address, Account? account) in accounts) tree.Set(address, account);
    }

    /// <summary>One process-wide thread that runs early account passes.</summary>
    private sealed class Applier
    {
        private const int SchedIdle = 5;
        private const int YieldsBeforeSleeping = 64;
        private const int SleepsBeforeParking = 50;

        public static readonly Applier Instance = new();

        private readonly ConcurrentQueue<EarlyAccountTrie> _queue = new();
        private readonly SemaphoreSlim _wake = new(0);
        private int _parked;

        private Applier()
        {
            Thread thread = new(Run) { IsBackground = true, Name = "Account early apply" };
            thread.Start();
        }

        public void Enqueue(EarlyAccountTrie trie)
        {
            _queue.Enqueue(trie);
            if (Volatile.Read(ref _parked) != 0 && Interlocked.Exchange(ref _parked, 0) != 0) _wake.Release();
        }

        private void Run()
        {
            if (ExperimentKnobs.EarlyAccountIdlePriority) LowerPriority();

            int idleRounds = 0;
            while (true)
            {
                if (_queue.TryDequeue(out EarlyAccountTrie? trie))
                {
                    idleRounds = 0;
                    try
                    {
                        trie.Apply();
                    }
                    catch (Exception)
                    {
                        // The trie marked itself claimed; its block sets every account at its end.
                    }

                    continue;
                }

                if (++idleRounds < YieldsBeforeSleeping) Thread.Yield();
                else if (idleRounds < YieldsBeforeSleeping + SleepsBeforeParking) Thread.Sleep(1);
                else
                {
                    Interlocked.Exchange(ref _parked, 1);
                    if (_queue.IsEmpty) _wake.Wait();
                    Volatile.Write(ref _parked, 0);
                    idleRounds = 0;
                }
            }
        }

        private static void LowerPriority()
        {
            try
            {
                SchedParam param = default;
                if (OperatingSystem.IsLinux() && sched_setscheduler(0, SchedIdle, ref param) == 0) return;
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SchedParam
        {
            public int SchedPriority;
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);
    }
}
