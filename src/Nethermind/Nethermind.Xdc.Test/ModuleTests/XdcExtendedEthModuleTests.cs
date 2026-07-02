// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Specs;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RPC;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture]
public class XdcExtendedEthModuleTests
{
    [Test]
    public async Task eth_getOwnerByCoinbase_returns_owner()
    {
        IXdcExtendedEthRpcModule module = CreateOwnerModule(TestItem.AddressC);
        string json = await RpcTest.TestSerializedRequest(module, "eth_getOwnerByCoinbase", TestItem.AddressA, BlockParameter.Latest);
        Assert.That(json, Does.Contain("result"));
        Assert.That(json, Does.Contain(TestItem.AddressC.ToString().ToLowerInvariant()));
    }

    [Test]
    public async Task eth_getRewardByHash_returns_stored_epoch_rewards()
    {
        IRewardsStore rewardsStore = Substitute.For<IRewardsStore>();
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            ["rewards"] = new()
            {
                [TestItem.AddressA.ToString()] = new() { [TestItem.AddressB.ToString()] = "1000" },
            },
        };
        rewardsStore.TryGetEpochRewards(TestItem.KeccakA, out Arg.Any<Dictionary<string, Dictionary<string, Dictionary<string, string>>>?>())
            .Returns(x =>
            {
                x[1] = payload;
                return true;
            });

        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        header.Hash = TestItem.KeccakA;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(TestItem.KeccakA).Returns(header);

        IXdcExtendedEthRpcModule module = new XdcExtendedEthModule(
            blockFinder,
            Substitute.For<IReceiptFinder>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IMasternodeVotingContract>(),
            rewardsStore);

        ResultWrapper<Dictionary<string, Dictionary<string, Dictionary<string, string>>>> result =
            await module.eth_getRewardByHash(TestItem.KeccakA);
        Assert.That(result.Data, Does.ContainKey("rewards"));
        Assert.That(result.Data!["rewards"], Does.ContainKey(TestItem.AddressA.ToString()));
    }

    [Test]
    public async Task eth_getTransactionAndReceiptProof_returns_null_when_tx_is_unknown()
    {
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(TestItem.KeccakA).Returns((Hash256?)null);

        IXdcExtendedEthRpcModule module = new XdcExtendedEthModule(
            Substitute.For<IBlockFinder>(),
            receiptFinder,
            Substitute.For<ISpecProvider>(),
            Substitute.For<IMasternodeVotingContract>(),
            Substitute.For<IRewardsStore>());

        ResultWrapper<XdcTransactionAndReceiptProof?> result = await module.eth_getTransactionAndReceiptProof(TestItem.KeccakA);
        Assert.That(result.Data, Is.Null);
    }

    [Test]
    public async Task eth_getTransactionAndReceiptProof_KnownTransaction_ReturnsZeroXPrefixedProofData()
    {
        Transaction firstTransaction = Build.A.Transaction
            .WithNonce(0)
            .WithHash(TestItem.KeccakA)
            .TestObject;
        Transaction targetTransaction = Build.A.Transaction
            .WithNonce(1)
            .WithHash(TestItem.KeccakB)
            .TestObject;
        Block block = Build.A.Block
            .WithTransactions(firstTransaction, targetTransaction)
            .TestObject;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithTransactionHash(firstTransaction.Hash).WithStatusCode(1).TestObject,
            Build.A.Receipt.WithTransactionHash(targetTransaction.Hash).WithStatusCode(1).TestObject,
        ];

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(block.Hash!).Returns(block);

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(targetTransaction.Hash!).Returns(block.Hash);
        receiptFinder.Get(block).Returns(receipts);

        IXdcExtendedEthRpcModule module = new XdcExtendedEthModule(
            blockFinder,
            receiptFinder,
            MainnetSpecProvider.Instance,
            Substitute.For<IMasternodeVotingContract>(),
            Substitute.For<IRewardsStore>());

        ResultWrapper<XdcTransactionAndReceiptProof?> result =
            await module.eth_getTransactionAndReceiptProof(targetTransaction.Hash!);

        Assert.That(result.Data, Is.Not.Null);
        XdcTransactionAndReceiptProof proof = result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.Key, Is.EqualTo("0x01"));
            Assert.That(proof.TxProofValues, Is.Not.Empty);
            Assert.That(proof.TxProofValues, Has.All.Matches<string>(value => value.StartsWith("0x", System.StringComparison.Ordinal)));
            Assert.That(proof.ReceiptProofValues, Is.Not.Empty);
            Assert.That(proof.ReceiptProofValues, Has.All.Matches<string>(value => value.StartsWith("0x", System.StringComparison.Ordinal)));
        }
    }

    private static IXdcExtendedEthRpcModule CreateOwnerModule(Address owner)
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        blockFinder.FindHeader(BlockParameter.Latest).Returns(header);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(header, TestItem.AddressA).Returns(owner);

        return new XdcExtendedEthModule(
            blockFinder,
            Substitute.For<IReceiptFinder>(),
            Substitute.For<ISpecProvider>(),
            votingContract,
            Substitute.For<IRewardsStore>());
    }
}
