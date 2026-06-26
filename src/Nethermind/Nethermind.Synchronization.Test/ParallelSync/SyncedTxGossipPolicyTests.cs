// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

public class SyncedTxGossipPolicyTests
{
    [TestCase(SyncMode.FastSync, ExpectedResult = false)]
    [TestCase(SyncMode.StateNodes, ExpectedResult = false)]
    [TestCase(SyncMode.FastSync | SyncMode.StateNodes, ExpectedResult = false)]
    [TestCase(SyncMode.FastHeaders, ExpectedResult = false)]
    [TestCase(SyncMode.BeaconHeaders, ExpectedResult = false)]
    [TestCase(SyncMode.FastHeaders | SyncMode.BeaconHeaders | SyncMode.StateNodes, ExpectedResult = false)]
    [TestCase(SyncMode.WaitingForBlock, ExpectedResult = true)]
    [TestCase(SyncMode.FastBodies | SyncMode.WaitingForBlock, ExpectedResult = true)]
    [TestCase(SyncMode.FastReceipts | SyncMode.WaitingForBlock, ExpectedResult = true)]
    [TestCase(SyncMode.FastBodies | SyncMode.FastSync, ExpectedResult = false)]
    [TestCase(SyncMode.Full, ExpectedResult = false)]
    [TestCase(SyncMode.DbLoad, ExpectedResult = false)]
    [TestCase(SyncMode.Disconnected, ExpectedResult = false)]
    [TestCase(SyncMode.UpdatingPivot, ExpectedResult = false)]
    public bool can_gossip(SyncMode mode) =>
        ((ITxGossipPolicy)new SyncedTxGossipPolicy(new StaticSelector(mode))).ShouldListenToGossipedTransactions;

    [TestCase(SyncMode.FastHeaders, ExpectedResult = true)]
    [TestCase(SyncMode.FastBodies, ExpectedResult = true)]
    [TestCase(SyncMode.FastReceipts, ExpectedResult = true)]
    [TestCase(SyncMode.FastBlockAccessLists, ExpectedResult = false)]
    public bool receipts_unsynced_ignores_block_access_lists_only_mode(SyncMode mode) =>
        mode.HaveNotSyncedReceiptsYet();

    [TestCase(SyncMode.FastHeaders, ExpectedResult = true)]
    [TestCase(SyncMode.FastBodies, ExpectedResult = false)]
    [TestCase(SyncMode.FastReceipts, ExpectedResult = false)]
    [TestCase(SyncMode.FastBlockAccessLists, ExpectedResult = true)]
    [TestCase(SyncMode.FastBodies | SyncMode.FastBlockAccessLists, ExpectedResult = true)]
    [TestCase(SyncMode.FastSync, ExpectedResult = true)]
    [TestCase(SyncMode.StateNodes, ExpectedResult = true)]
    [TestCase(SyncMode.BeaconHeaders, ExpectedResult = true)]
    [TestCase(SyncMode.UpdatingPivot, ExpectedResult = true)]
    [TestCase(SyncMode.Full, ExpectedResult = false)]
    [TestCase(SyncMode.WaitingForBlock, ExpectedResult = false)]
    public bool block_access_lists_unsynced_tracks_headers_state_and_bal_phases(SyncMode mode) =>
        mode.HaveNotSyncedBlockAccessListsYet();

    [Test]
    public void Composite_reflects_sync_mode_transitions()
    {
        MutableSelector selector = new(SyncMode.FastSync);
        SyncedTxGossipPolicy syncPolicy = new(selector);
        CompositeTxGossipPolicy composite = new(new FixedTxGossipPolicySource([syncPolicy]));

        // During sync: gossip disabled
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.False);
        Assert.That(composite.CanGossipTransactions, Is.True);

        // Transition to synced: gossip must become enabled
        selector.Current = SyncMode.WaitingForBlock;
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.True);

        // Transition back to sync: gossip must become disabled again
        selector.Current = SyncMode.StateNodes;
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.False);
    }

    [Test]
    public void Accept_tx_when_not_synced_allows_gossip_listening()
    {
        TxPoolConfig txPoolConfig = new() { AcceptTxWhenNotSynced = true };
        MutableSelector selector = new(SyncMode.Disconnected);
        SyncedTxGossipPolicy syncPolicy = new(selector, txPoolConfig);
        CompositeTxGossipPolicy composite = new(new FixedTxGossipPolicySource([syncPolicy]));

        Assert.That(composite.ShouldListenToGossipedTransactions, Is.True);

        selector.Current = SyncMode.FastSync;
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.True);
    }

    private class MutableSelector(SyncMode initial) : ISyncModeSelector
    {
        private SyncMode _current = initial;

        public SyncMode Current
        {
            get => _current;
            set
            {
                SyncMode previous = _current;
                _current = value;
                Changed?.Invoke(this, new SyncModeChangedEventArgs(previous, value));
            }
        }

        public event EventHandler<SyncModeChangedEventArgs> Preparing { add { } remove { } }
        public event EventHandler<SyncModeChangedEventArgs> Changing { add { } remove { } }
        public event EventHandler<SyncModeChangedEventArgs>? Changed;
        public Task StopAsync() => Task.CompletedTask;
        public Task StartAsync() => Task.CompletedTask;
        public void Update() { }
        public void Dispose() { }
    }

    private class FixedTxGossipPolicySource(ITxGossipPolicy[] policies) : ITxGossipPolicySource
    {
        public ITxGossipPolicy[] Policies => policies;
    }
}
