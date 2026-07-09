// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.State.Proofs;
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
        XdcEpochRewards payload = new()
        {
            Rewards = new()
            {
                [TestItem.AddressA.ToString()] = new() { [TestItem.AddressB.ToString()] = "1000" },
            },
            Signers = new()
            {
                [TestItem.AddressA.ToString()] = new XdcRewardLog { Sign = 1, Reward = "1000" },
            },
        };
        rewardsStore.TryGetEpochRewards(TestItem.KeccakA, out Arg.Any<XdcEpochRewards?>())
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

        ResultWrapper<XdcEpochRewards> result =
            await module.eth_getRewardByHash(TestItem.KeccakA);
        Assert.That(result.Data!.Rewards, Does.ContainKey(TestItem.AddressA.ToString()));
        Assert.That(result.Data.Signers[TestItem.AddressA.ToString()].Sign, Is.EqualTo(1));
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
    public async Task eth_getTransactionAndReceiptProof_returns_valid_proof_for_known_transaction()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithTransactionHash(TestItem.KeccakB).TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        Hash256 blockHash = block.Hash!;

        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(TestItem.KeccakB).Returns(blockHash);
        receiptFinder.Get(block).Returns([receipt]);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(blockHash).Returns(block);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(block.Header).Returns(releaseSpec);

        IXdcExtendedEthRpcModule module = new XdcExtendedEthModule(
            blockFinder,
            receiptFinder,
            specProvider,
            Substitute.For<IMasternodeVotingContract>(),
            Substitute.For<IRewardsStore>());

        ResultWrapper<XdcTransactionAndReceiptProof?> result = await module.eth_getTransactionAndReceiptProof(TestItem.KeccakB);

        Assert.That(result.Data, Is.Not.Null);
        XdcTransactionAndReceiptProof proof = result.Data!;
        Assert.That(proof.BlockHash, Is.EqualTo(blockHash));
        Assert.That(proof.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(block.Transactions)));
        Assert.That(proof.TxProofKeys, Has.Length.GreaterThan(0));
        Assert.That(proof.TxProofValues, Has.Length.EqualTo(proof.TxProofKeys.Length));
        Assert.That(proof.ReceiptProofKeys, Has.Length.GreaterThan(0));
        Assert.That(proof.ReceiptProofValues, Has.Length.EqualTo(proof.ReceiptProofKeys.Length));
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
