// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        ((ITxGossipPolicy)new SyncedTxGossipPolicy(new StaticSelector(mode))).ShouldListenToGossippedTransactions;
}
