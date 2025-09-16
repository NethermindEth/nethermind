// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

internal class Create2DeployerContractRewriterTests
{
    [Test]
    public void Test_Create2DeployerContractRewriter_Ensures_Preinstallation()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(2).TestObject;
        BlockHeader canyonHeader = blockTree.FindHeader(1, BlockTreeLookupOptions.None)!;

        OptimismSpecHelper specHelper = new(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = canyonHeader.Timestamp,
        });

        IWorldState ws = TestWorldStateFactory.CreateForTest();
        using var _ = ws.BeginScope(IWorldState.PreGenesis);

        Create2DeployerContractRewriter rewriter = new(specHelper, new TestSingleReleaseSpecProvider(Cancun.Instance), blockTree);

        rewriter.RewriteContract(blockTree.FindHeader(1, BlockTreeLookupOptions.None)!, ws);

        byte[] setCode = ws.GetCode(PreInstalls.Create2Deployer)!;
        Assert.That(setCode, Is.Not.Empty);
    }
}
