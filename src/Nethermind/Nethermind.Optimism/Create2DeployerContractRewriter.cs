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
    private IOPConfigHelper _opConfigHelper;
    private ISpecProvider _specProvider;
    private IBlockTree _blockTree;

    public Create2DeployerContractRewriter(IOPConfigHelper opConfigHelper, ISpecProvider specProvider, IBlockTree blockTree)
    {
        _opConfigHelper = opConfigHelper;
        _specProvider = specProvider;
        _blockTree = blockTree;
    }

    public void RewriteContract(BlockHeader header, IWorldState worldState)
    {
        IReleaseSpec spec = _specProvider.GetSpec(header);
        BlockHeader? parent = _blockTree.FindParent(header, BlockTreeLookupOptions.None)?.Header;
        if ((parent is null || !_opConfigHelper.IsCanyon(parent)) && _opConfigHelper.IsCanyon(header))
        {
            worldState.InsertCode(_opConfigHelper.Create2DeployerAddress!, _opConfigHelper.Create2DeployerCode, spec);
        }
    }
}
