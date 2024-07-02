// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Optimism;

public class Create2DeployerContractRewriter(IOptimismSpecHelper opSpecHelper, ISpecProvider specProvider, IBlockTree blockTree)
{
    public void RewriteContract(BlockHeader header, IWorldState worldState)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        BlockHeader? parent = blockTree.FindParent(header, BlockTreeLookupOptions.None)?.Header;

        // A migration at the first block of Canyon unless it's genesis
        if ((parent is null || !opSpecHelper.IsCanyon(parent)) && opSpecHelper.IsCanyon(header) && !header.IsGenesis)
        {
            worldState.InsertCode(opSpecHelper.Create2DeployerAddress!, opSpecHelper.Create2DeployerCode, spec);
        }
    }
}
