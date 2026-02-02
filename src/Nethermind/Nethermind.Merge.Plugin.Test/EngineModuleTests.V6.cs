// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task Builds_block_with_BAL()
    {
        using MergeTestBlockchain chain = await new MergeTestBlockchain().Build(new TestSpecProvider(Amsterdam.Instance));

        Block genesis = chain.BlockFinder.FindGenesisBlock()!;
        PayloadAttributes payloadAttributes =
            new() { Timestamp = 12, PrevRandao = genesis.Header.Random!, SuggestedFeeRecipient = Address.Zero };

        // inject tx into txpool, use fcu
        string payloadId = chain.PayloadPreparationService!.StartPreparingPayload(genesis.Header, payloadAttributes)!;

        await Task.Delay(1000);

        ResultWrapper<GetPayloadV6Result?> getPayloadResult =
            await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(payloadId));
        var res = getPayloadResult.Data!;
        Assert.That(res.ExecutionPayload.BlockAccessList, Is.Not.Null);
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(new Rlp(res.ExecutionPayload.BlockAccessList));
        //use balbuilder.withpreexecutionprecompiles
    }
}