// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task Builds_block_with_BAL()
    {
        ulong timestamp = 12;
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        Block genesis = chain.BlockFinder.FindGenesisBlock()!;
        PayloadAttributes payloadAttributes = new() {
            Timestamp = timestamp,
            PrevRandao = genesis.Header.Random!,
            SuggestedFeeRecipient = Address.Zero,
            ParentBeaconBlockRoot = Keccak.Zero,
            Withdrawals = []
        };

        // inject tx into txpool
        ForkchoiceStateV1 fcuState = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResponse = await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcuState, payloadAttributes);
        Assert.That(fcuResponse.Result.ResultType, Is.EqualTo(ResultType.Success));

        await Task.Delay(1000);

        ResultWrapper<GetPayloadV6Result?> getPayloadResult =
            await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcuResponse.Data.PayloadId!));
        GetPayloadV6Result res = getPayloadResult.Data!;
        Assert.That(res.ExecutionPayload.BlockAccessList, Is.Not.Null);
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(new Rlp(res.ExecutionPayload.BlockAccessList));
        Assert.That(bal, Is.EqualTo(Build.A.BlockAccessList.WithPrecompileChanges(genesis.Header.Hash!, timestamp).TestObject));
    }

    [Test]
    public async Task Can_get_payload_bodies_by_hash_v2()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        for (var i = 1; i < 5; i++)
        {
            await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
        }

        ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByHashV2([
            new Hash256("0x415a91edfcb5cc6b432b9ec556b5b5b7e6a0b2d0f2d93a281995730c8fd53213"),
            new Hash256("0xfa59ce89da70514dce8c4e04daa90e0a7b7612f8c45af4ffa46a39a32466710d"),
            Hash256.Zero
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(3));
            Assert.That(response.Data.First()!.BlockAccessList!.Count, Is.EqualTo(310));
            Assert.That(response.Data.ElementAt(2), Is.Null);
        }
    }

    [Test]
    public async Task Can_get_payload_bodies_by_range_v2()
    {
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        using MergeTestBlockchain chain = await CreateBlockchain(specProvider);

        for (var i = 1; i < 5; i++)
        {
            await AddNewBlockV6(chain.EngineRpcModule, chain, 1);
        }

        ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> response = await chain.EngineRpcModule.engine_getPayloadBodiesByRangeV2(1, 6);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(response.Data.Count, Is.EqualTo(4)); // cutoff at head
            Assert.That(response.Data.First()!.BlockAccessList!.Count, Is.EqualTo(310));
        }
    }

    private async Task<ExecutionPayloadV3> AddNewBlockV6(IEngineRpcModule rpcModule, MergeTestBlockchain chain, int transactionCount = 0)
    {
        Transaction[] txs = BuildTransactions(chain, chain.BlockTree.Head!.Hash!, TestItem.PrivateKeyA, TestItem.AddressB, (uint)transactionCount, 0, out _, out _, 0);
        chain.AddTransactions(txs);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE
        };
        Hash256 currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);

        Task blockImprovementWait = chain.WaitForImprovedBlock();

        string payloadId = (await rpcModule.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes)).Data.PayloadId!;

        await blockImprovementWait;

        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpcModule.engine_getPayloadV6(Bytes.FromHexString(payloadId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payloadResult.Result, Is.EqualTo(Result.Success));
            Assert.That(payloadResult.Data, Is.Not.Null);
        }

        GetPayloadV6Result payload = payloadResult.Data;
        await rpcModule.engine_newPayloadV5(payload.ExecutionPayload, payload.BlobsBundle.Blobs, TestItem.KeccakE, []);

        ForkchoiceStateV1 newForkchoiceState = new(payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash, payload.ExecutionPayload.BlockHash);
        await rpcModule.engine_forkchoiceUpdatedV3(newForkchoiceState, null);

        return payload.ExecutionPayload;
    }

}