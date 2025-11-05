// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Consensus;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcReorgModuleTests
{
    [Test]
    public async Task TestNormalReorgWhenNotInvolveCommittedBlock()
    {
        var blockChain = await XdcTestBlockchain.Create();
        var headParent = blockChain.BlockTree.FindHeader(blockChain.BlockTree.Head!.ParentHash!);

        var forkBlock = await blockChain.BlockProducer.BuildBlock(headParent);

        blockChain.BlockTree.SuggestBlock(forkBlock!);

    }

}
