// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

internal class Create2DeployerContractRewriterTests
{
    [Test]
    public void Test_Create2DeployerContractRewriter_Ensures_Preinstallation()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(2).TestObject;
        BlockHeader? canyonHeader = blockTree.FindHeader(1, BlockTreeLookupOptions.None);
        Assert.That(canyonHeader, Is.Not.Null, "Canyon header should not be null");

        // Use a default timestamp if canyonHeader is null (which shouldn't happen due to the assertion)
        ulong canyonTimestamp = canyonHeader != null ? canyonHeader.Timestamp : 0;
        
        OptimismSpecHelper specHelper = new(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = canyonTimestamp,
        });

        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState ws = worldStateManager.GlobalWorldState;

        Create2DeployerContractRewriter rewriter = new(specHelper, new TestSingleReleaseSpecProvider(Cancun.Instance), blockTree);

        BlockHeader? header = blockTree.FindHeader(1, BlockTreeLookupOptions.None);
        Assert.That(header, Is.Not.Null, "Header should not be null");
        if (header != null)
        {
            rewriter.RewriteContract(header, ws);
        }

        byte[]? setCode = ws.GetCode(PreInstalls.Create2Deployer);
        Assert.That(setCode, Is.Not.Null, "Code should not be null");
        Assert.That(setCode, Is.Not.Empty, "Code should not be empty");
    }
}
