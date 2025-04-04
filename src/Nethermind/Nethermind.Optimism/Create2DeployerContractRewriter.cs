// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;
using System;
using System.Linq;

namespace Nethermind.Optimism;

public class Create2DeployerContractRewriter(IOptimismSpecHelper opSpecHelper, ISpecProvider specProvider, IBlockTree blockTree)
{
    public void RewriteContract(BlockHeader header, IWorldState worldState)
    {
        BlockHeader? parent = blockTree.FindParent(header, BlockTreeLookupOptions.None)?.Header;

        // A migration at the first block of Canyon unless it's genesis
        if ((parent is null || !opSpecHelper.IsCanyon(parent)) && opSpecHelper.IsCanyon(header) && !header.IsGenesis)
        {
            IReleaseSpec spec = specProvider.GetSpec(header);

            var code = GetCreate2DeployerCode();

            worldState.CreateAccountIfNotExists(PreInstalls.Create2Deployer, 0);
            worldState.InsertCode(PreInstalls.Create2Deployer, code, spec);
        }
    }

    private static byte[] GetCreate2DeployerCode()
    {
        var asm = typeof(Create2DeployerContractRewriter).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(name => name.EndsWith("Create2Deployer.data"));

        using var stream = asm.GetManifestResourceStream(name);
        var buffer = new byte[stream!.Length];
        stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }
}
