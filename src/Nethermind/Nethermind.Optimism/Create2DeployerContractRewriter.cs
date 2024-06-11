// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Optimism;

public class Create2DeployerContractRewriter
{
    private readonly IOptimismSpecHelper _opSpecHelper;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;

    public Create2DeployerContractRewriter(IOptimismSpecHelper opSpecHelper, ISpecProvider specProvider, IBlockTree blockTree)
    {
        _opSpecHelper = opSpecHelper;
        _specProvider = specProvider;
        _blockTree = blockTree;
    }

    public void RewriteContract(BlockHeader header, IWorldState worldState)
    {
        IReleaseSpec spec = _specProvider.GetSpec(header);
        BlockHeader? parent = _blockTree.FindParent(header, BlockTreeLookupOptions.None)?.Header;
        if ((parent is null || !_opSpecHelper.IsCanyon(parent)) && _opSpecHelper.IsCanyon(header))
        {
            worldState.InsertCode(_opSpecHelper.Create2DeployerAddress!, _opSpecHelper.Create2DeployerCode, spec);
        }
    }
}
