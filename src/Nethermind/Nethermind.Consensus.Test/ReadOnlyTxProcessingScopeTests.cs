// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ReadOnlyTxProcessingScopeTests
{
    [Test]
    public void Test_WhenDispose_ThenStateRootWillRevert()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.StateRoot.Returns(TestItem.KeccakB);
        ReadOnlyTxProcessingScope env = new ReadOnlyTxProcessingScope(
            Substitute.For<ITransactionProcessor>(),
            worldState
        );

        env.Init(Keccak.EmptyTreeHash);
        env.Dispose();

        env.WorldState.Received().StateRoot = TestItem.KeccakB;
    }
}
