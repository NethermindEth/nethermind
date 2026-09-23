// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
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

        IXdcExtendedEthRpcModule module = CreateModule(blockFinder: blockFinder, rewardsStore: rewardsStore);

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

        IXdcExtendedEthRpcModule module = CreateModule(receiptFinder: receiptFinder);

        ResultWrapper<XdcTransactionAndReceiptProof?> result = await module.eth_getTransactionAndReceiptProof(TestItem.KeccakA);
        Assert.That(result.Data, Is.Null);
    }

    [Test]
    public async Task eth_getTransactionAndReceiptProof_returns_valid_proof_for_known_transaction()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithTransactionHash(TestItem.KeccakB).TestObject;
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(releaseSpec, [receipt], Rlp.GetDecoder<TxReceipt>()!);
        Block block = Build.A.Block.WithTransactions(tx).WithReceiptsRoot(receiptsRoot).TestObject;
        Hash256 blockHash = block.Hash!;

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(TestItem.KeccakB).Returns(blockHash);
        receiptFinder.Get(block).Returns([receipt]);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(blockHash).Returns(block);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(block.Header).Returns(releaseSpec);

        IXdcExtendedEthRpcModule module = CreateModule(
            blockFinder: blockFinder,
            receiptFinder: receiptFinder,
            specProvider: specProvider);

        ResultWrapper<XdcTransactionAndReceiptProof?> result = await module.eth_getTransactionAndReceiptProof(TestItem.KeccakB);

        Assert.That(result.Data, Is.Not.Null);
        XdcTransactionAndReceiptProof proof = result.Data!;
        Assert.That(proof.BlockHash, Is.EqualTo(blockHash));
        Assert.That(proof.TxRoot, Is.EqualTo(block.Header.TxRoot));
        Assert.That(proof.ReceiptRoot, Is.EqualTo(receiptsRoot));
        Assert.That(proof.TxProofKeys, Has.Length.GreaterThan(0));
        Assert.That(proof.TxProofValues, Has.Length.EqualTo(proof.TxProofKeys.Length));
        Assert.That(proof.ReceiptProofKeys, Has.Length.GreaterThan(0));
        Assert.That(proof.ReceiptProofValues, Has.Length.EqualTo(proof.ReceiptProofKeys.Length));
    }

    [Test]
    public async Task eth_getAccountInfo_reports_a_contract_by_code_hash_and_size()
    {
        byte[] code = [0x60, 0x60, 0x60, 0x40];
        ValueHash256 codeHash = ValueKeccak.Compute(code);
        ValueHash256 storageRoot = TestItem.KeccakC.ValueHash256;
        AccountStruct account = new(7UL, 1234, storageRoot, codeHash);

        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader _) = StateWith(TestItem.AddressA, account);
        stateReader.GetCode(codeHash).Returns(code);

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(blockFinder: blockFinder, stateReader: stateReader)
                .eth_getAccountInfo(TestItem.AddressA, BlockParameter.Latest);

        XdcAccountInfo info = result.Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.Address, Is.EqualTo(TestItem.AddressA));
            Assert.That(info.Balance, Is.EqualTo((UInt256)1234));
            Assert.That(info.Nonce, Is.EqualTo(7UL));
            Assert.That(info.CodeHash, Is.EqualTo(new Hash256(codeHash)));
            Assert.That(info.CodeSize, Is.EqualTo(code.Length));
            Assert.That(info.StorageHash, Is.EqualTo(new Hash256(storageRoot)));
        }
    }

    /// <remarks>
    /// The field names are the reason this endpoint exists, so pin them on the wire rather than on the DTO.
    /// </remarks>
    [Test]
    public async Task eth_getAccountInfo_serializes_the_field_names_the_reference_uses()
    {
        AccountStruct account = new(7UL, 1234, TestItem.KeccakC.ValueHash256, ValueKeccak.Compute([0x60]));
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader _) = StateWith(TestItem.AddressA, account);
        stateReader.GetCode(Arg.Any<ValueHash256>()).Returns([0x60]);

        string json = await RpcTest.TestSerializedRequest<IXdcExtendedEthRpcModule>(
            CreateModule(blockFinder: blockFinder, stateReader: stateReader),
            "eth_getAccountInfo", TestItem.AddressA, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            foreach (string field in new[] { "address", "balance", "nonce", "codeHash", "codeSize", "storageHash" })
            {
                Assert.That(json, Does.Contain($"\"{field}\""), field);
            }

            // The reference reports the code's hash and length in place of the code itself.
            Assert.That(json, Does.Not.Contain("\"code\":"));

            // Quantities stay hex-encoded, as everywhere else in this client's JSON-RPC, even though the
            // reference emits them as JSON numbers.
            Assert.That(json, Does.Contain("\"nonce\":\"0x7\""));
            Assert.That(json, Does.Contain("\"codeSize\":\"0x1\""));
        }
    }

    /// <remarks>
    /// Reporting zero here would be indistinguishable from an externally owned account, so a code hash the
    /// store cannot resolve has to surface as a failure.
    /// </remarks>
    [Test]
    public async Task eth_getAccountInfo_fails_when_the_code_is_missing_from_the_store([Values] bool syncingState)
    {
        AccountStruct account = new(1UL, 5, TestItem.KeccakC.ValueHash256, ValueKeccak.Compute([0x60]));
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader _) = StateWith(TestItem.AddressA, account);
        stateReader.GetCode(Arg.Any<ValueHash256>()).Returns((byte[]?)null);

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(
                blockFinder: blockFinder,
                stateReader: stateReader,
                ethSyncingInfo: SyncingState(syncingState ? SyncMode.StateNodes : SyncMode.Full))
                .eth_getAccountInfo(TestItem.AddressA, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(result.IsTemporary, Is.EqualTo(syncingState));
        }
    }

    /// <remarks>Every externally owned account takes this path, so it must not touch the code store.</remarks>
    [Test]
    public async Task eth_getAccountInfo_does_not_read_code_for_an_account_that_has_none()
    {
        AccountStruct account = new(3UL, 99);
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader _) = StateWith(TestItem.AddressA, account);

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(blockFinder: blockFinder, stateReader: stateReader)
                .eth_getAccountInfo(TestItem.AddressA, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.CodeSize, Is.Zero);
            Assert.That(result.Data.Nonce, Is.EqualTo(3UL));
        }

        stateReader.DidNotReceive().GetCode(Arg.Any<ValueHash256>());
    }

    /// <remarks>
    /// The reference reports an address with no account as all zeros, including zero hashes rather than the
    /// empty-code and empty-trie hashes, which is what tells a caller the account is absent.
    /// </remarks>
    [Test]
    public async Task eth_getAccountInfo_reports_an_absent_account_with_zero_hashes()
    {
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader header) = HeadWithState();
        stateReader.TryGetAccount(header, TestItem.AddressD, out Arg.Any<AccountStruct>()).Returns(false);

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(blockFinder: blockFinder, stateReader: stateReader)
                .eth_getAccountInfo(TestItem.AddressD, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Address, Is.EqualTo(TestItem.AddressD));
            Assert.That(result.Data.Balance, Is.EqualTo(UInt256.Zero));
            Assert.That(result.Data.CodeHash, Is.EqualTo(Hash256.Zero));
            Assert.That(result.Data.StorageHash, Is.EqualTo(Hash256.Zero));
            Assert.That(result.Data.CodeSize, Is.Zero);
        }
    }

    /// <remarks>
    /// Only a header the node has not reached yet is an expected miss, so both halves of that condition have
    /// to hold: a request the node cannot serve at all stays a warning however far behind it is.
    /// </remarks>
    [Test]
    public async Task eth_getAccountInfo_marks_a_header_miss_temporary_only_while_headers_sync(
        [Values] bool headerNotFound, [Values] bool syncingHeaders)
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        // A head with no matching header is the ResourceNotFound branch; no head at all is InternalError.
        if (headerNotFound)
        {
            blockFinder.Head.Returns(Build.A.Block.TestObject);
        }

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(
                blockFinder: blockFinder,
                ethSyncingInfo: SyncingState(syncingHeaders ? SyncMode.FastHeaders : SyncMode.Full))
                .eth_getAccountInfo(TestItem.AddressA, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(headerNotFound ? ErrorCodes.ResourceNotFound : ErrorCodes.InternalError));
            Assert.That(result.IsTemporary, Is.EqualTo(headerNotFound && syncingHeaders));
        }
    }

    /// <remarks>
    /// A node still fetching state is expected to miss it, so the failure is marked temporary and the
    /// response suppresses the warning the caller would otherwise log on every call during sync.
    /// </remarks>
    [Test]
    public async Task eth_getAccountInfo_fails_when_the_block_has_no_state([Values] bool syncingState)
    {
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader _) = HeadWithState(hasState: false);

        ResultWrapper<XdcAccountInfo> result =
            await CreateModule(
                blockFinder: blockFinder,
                stateReader: stateReader,
                ethSyncingInfo: SyncingState(syncingState ? SyncMode.StateNodes : SyncMode.Full))
                .eth_getAccountInfo(TestItem.AddressA, BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(result.IsTemporary, Is.EqualTo(syncingState));
        }
    }

    private static IEthSyncingInfo SyncingState(SyncMode syncMode)
    {
        IEthSyncingInfo ethSyncingInfo = Substitute.For<IEthSyncingInfo>();
        ethSyncingInfo.SyncMode.Returns(syncMode);
        return ethSyncingInfo;
    }

    /// <summary>Points the finder at a head block and the reader at whether that block's state is retained.</summary>
    private static (IStateReader StateReader, IBlockFinder BlockFinder, BlockHeader Header) HeadWithState(bool hasState = true)
    {
        BlockHeader header = Build.A.XdcBlockHeader().TestObject;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(BlockParameter.Latest, Arg.Any<bool>()).Returns(header);
        blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(header).Returns(hasState);

        return (stateReader, blockFinder, header);
    }

    /// <summary>Adds <paramref name="account"/> at <paramref name="address"/> to the head block's state.</summary>
    private static (IStateReader StateReader, IBlockFinder BlockFinder, BlockHeader Header) StateWith(
        Address address, AccountStruct account)
    {
        (IStateReader stateReader, IBlockFinder blockFinder, BlockHeader header) = HeadWithState();
        stateReader.TryGetAccount(header, address, out Arg.Any<AccountStruct>())
            .Returns(x => { x[2] = account; return true; });

        return (stateReader, blockFinder, header);
    }

    /// <summary>Builds the module with substitutes for everything the caller does not pin down.</summary>
    private static XdcExtendedEthModule CreateModule(
        IBlockFinder? blockFinder = null,
        IReceiptFinder? receiptFinder = null,
        ISpecProvider? specProvider = null,
        IMasternodeVotingContract? votingContract = null,
        IRewardsStore? rewardsStore = null,
        IStateReader? stateReader = null,
        IEthSyncingInfo? ethSyncingInfo = null) =>
        new(blockFinder ?? Substitute.For<IBlockFinder>(),
            receiptFinder ?? Substitute.For<IReceiptFinder>(),
            specProvider ?? Substitute.For<ISpecProvider>(),
            votingContract ?? Substitute.For<IMasternodeVotingContract>(),
            rewardsStore ?? Substitute.For<IRewardsStore>(),
            stateReader ?? Substitute.For<IStateReader>(),
            ethSyncingInfo ?? Substitute.For<IEthSyncingInfo>());

    private static IXdcExtendedEthRpcModule CreateOwnerModule(Address owner)
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        blockFinder.FindHeader(BlockParameter.Latest).Returns(header);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(header, TestItem.AddressA).Returns(owner);

        return CreateModule(blockFinder: blockFinder, votingContract: votingContract);
    }
}
