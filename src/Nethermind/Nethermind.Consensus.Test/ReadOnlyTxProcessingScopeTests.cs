// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ReadOnlyTxProcessingScopeTests
{
    /*
    [Test]
    public void Test_WhenDispose_ThenStateRootWillRevert()
    {
        ReadOnlyTxProcessingScope env = new ReadOnlyTxProcessingScope(
            Substitute.For<ITransactionProcessor>(),
            Substitute.For<IWorldState>(),
            TestItem.KeccakB
        );

        env.Dispose();

        env.WorldState.Received().StateRoot = TestItem.KeccakB;
    }
    */
}
