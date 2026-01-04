// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Simulate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test;

public class SimulateBlockhashProviderTests
{
    [Test]
    public void GetBlockhash_uses_requested_number_with_best_suggested_header()
    {
        IBlockhashProvider innerProvider = Substitute.For<IBlockhashProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        SimulateBlockhashProvider simulateProvider = new(innerProvider, blockTree);

        BlockHeader currentHeader = Build.A.BlockHeader.TestObject;
        BlockHeader bestSuggestedHeader = Build.A.BlockHeader.TestObject;
        long bestKnownNumber = 100;
        long requestedNumber = 109;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();

        blockTree.BestKnownNumber.Returns(bestKnownNumber);
        blockTree.BestSuggestedHeader.Returns(bestSuggestedHeader);

        simulateProvider.GetBlockhash(currentHeader, requestedNumber, spec);

        innerProvider.Received(1).GetBlockhash(bestSuggestedHeader, requestedNumber, spec);
    }
}
