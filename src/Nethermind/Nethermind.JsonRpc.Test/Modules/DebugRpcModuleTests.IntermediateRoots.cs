// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Diagnostic_replay_rejects_header_only_child_of_processed_head(
        [Values("debug_intermediateRoots", "debug_standardTraceBlockToFile")] string method,
        [Values(BlockTreeInsertHeaderOptions.None, BlockTreeInsertHeaderOptions.BeaconHeaderInsert)] BlockTreeInsertHeaderOptions insertOptions)
    {
        using Context context = await Context.Create();
        TestRpcBlockchain chain = context.Blockchain;
        BlockHeader head = chain.BlockTree.Head!.Header;
        BlockHeader child = Build.A.BlockHeader.WithParent(head).WithStateRoot(TestItem.KeccakH).TestObject;
        Assert.That(chain.BlockTree.Insert(child, insertOptions), Is.EqualTo(AddBlockResult.Added));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(child.Hash!, BlockTreeLookupOptions.None), Is.Null, "body is not stored");
            Assert.That(chain.WorldStateManager.GlobalStateReader.HasStateForBlock(head), Is.True, "processed parent has state");
        }

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, child.Hash!);

        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceNotFound), response);
    }

    [Test]
    public async Task Debug_intermediateRoots_returns_one_root_per_transaction()
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        Hash256 blockHash = context.Blockchain.BlockTree.Head!.Hash!;
        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(blockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Has.Count.EqualTo(2), "the block has exactly two user transactions");
        }
        Assert.That(result.Data, Is.Unique, "each tx mutates state, producing a distinct post-tx root");
        Assert.That(result.Data, Does.Not.Contain(Keccak.Zero));
    }

    [Test]
    public async Task Debug_intermediateRoots_stops_at_rejected_transaction_of_bad_block([Values] bool wrongNonce)
    {
        using Context context = await Context.Create();
        Block valid = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        ulong nonce = valid.Transactions[1].Nonce;
        Transaction rejected = Build.A.Transaction
            .WithNonce(wrongNonce ? nonce + 1 : nonce)
            .WithValue(wrongNonce ? UInt256.Zero : UInt256.MaxValue / 2)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Hash256 badHash = InsertBadBlock(context, valid, [valid.Transactions[0], rejected, valid.Transactions[1]]);

        IReadOnlyCollection<Hash256> validRoots = context.DebugRpcModule.debug_intermediateRoots(valid.Hash!).Data;
        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(badHash);

        Assert.That(result.Data, Is.EqualTo(new[] { validRoots.First() }), result.Result.Error);
    }

    private static Hash256 InsertBadBlock(Context context, Block sibling, Transaction[] transactions)
    {
        BlockHeader badHeader = sibling.Header.Clone();
        badHeader.StateRoot = TestItem.KeccakH;
        badHeader.Hash = badHeader.CalculateHash();
        Block bad = new(badHeader, new BlockBody(transactions, sibling.Uncles, sibling.Withdrawals));
        context.Blockchain.Container.Resolve<IBadBlockStore>().Insert(bad);
        return bad.Hash!;
    }

    [Test]
    public async Task Debug_intermediateRoots_rejects_genesis()
    {
        using Context context = await Context.Create();
        Hash256 genesisHash = context.Blockchain.BlockTree.Genesis!.Hash!;

        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(genesisHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(result.Result.Error, Does.Contain("genesis"));
        }
    }
}
