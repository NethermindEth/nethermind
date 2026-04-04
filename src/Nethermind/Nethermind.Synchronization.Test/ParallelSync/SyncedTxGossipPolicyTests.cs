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
    [TestCase(SyncMode.SnapSync, ExpectedResult = false)]
    [TestCase(SyncMode.StateNodes, ExpectedResult = false)]
    [TestCase(SyncMode.FastSync | SyncMode.StateNodes, ExpectedResult = false)]
    [TestCase(SyncMode.FastHeaders, ExpectedResult = false)]
    [TestCase(SyncMode.BeaconHeaders, ExpectedResult = false)]
    [TestCase(SyncMode.FastHeaders | SyncMode.BeaconHeaders | SyncMode.SnapSync, ExpectedResult = false)]
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

    [Test]
    public void Composite_reflects_sync_mode_transitions()
    {
        MutableSelector selector = new(SyncMode.FastSync);
        SyncedTxGossipPolicy syncPolicy = new(selector);
        CompositeTxGossipPolicy composite = new(new Lazy<ITxGossipPolicy[]>([syncPolicy]));

        // During sync: gossip disabled
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.False);
        Assert.That(composite.CanGossipTransactions, Is.True);

        // Transition to synced: gossip must become enabled
        selector.Current = SyncMode.WaitingForBlock;
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.True);

        // Transition back to sync: gossip must become disabled again
        selector.Current = SyncMode.SnapSync;
        Assert.That(composite.ShouldListenToGossipedTransactions, Is.False);
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
        public void Update() { }
        public void Dispose() { }
    }
}
