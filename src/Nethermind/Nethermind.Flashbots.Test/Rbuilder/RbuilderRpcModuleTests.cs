// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Flashbots.Modules.Rbuilder;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Flashbots.Data;
using Nethermind.JsonRpc;
using Nethermind.State;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Flashbots.Test.Rbuilder;

public class RbuilderRpcModuleTests
{
    private IRbuilderRpcModule _rbuilderRpcModule;
    private IWorldStateManager _worldStateManager;
    private IBlockTree _blockTree;
    private IContainer _container;

    [SetUp]
    public void Setup()
    {
        BlockTreeBuilder blockTree = Build.A.BlockTree()
            .OfChainLength(10);

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new FlashbotsModule(new FlashbotsConfig(), new JsonRpcConfig()))
            .AddSingleton<IBlockTree>(blockTree.TestObject)
            .AddKeyedSingleton(DbNames.BlockInfos, blockTree.BlockInfoDb)
            .AddKeyedSingleton(DbNames.Blocks, blockTree.BlocksDb)
            .AddKeyedSingleton(DbNames.Headers, blockTree.HeadersDb)
            .AddKeyedSingleton(DbNames.BlockNumbers, blockTree.BlockNumbersDb)
            .AddKeyedSingleton(DbNames.Metadata, blockTree.MetadataDb)
            .AddKeyedSingleton(DbNames.BadBlocks, blockTree.BadBlocksDb)
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .Build();

        _worldStateManager = _container.Resolve<IWorldStateManager>();
        _blockTree = _container.Resolve<IBlockTree>();
        _rbuilderRpcModule = _container.Resolve<IRbuilderRpcModule>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _container.DisposeAsync();
    }

    [Test]
    public async Task Test_getCodeByHash()
    {
        string theCode = "01234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567";
        byte[] theCodeBytes = Bytes.FromHexString(theCode);
        Hash256 theHash = Keccak.Compute(theCodeBytes);

        IWorldState worldState = _worldStateManager.GlobalWorldState;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 100000);
            worldState.InsertCode(TestItem.AddressA, theCodeBytes, London.Instance);
            worldState.Commit(London.Instance);
            worldState.CommitTree(0);
        }

        string response = await RpcTest.TestSerializedRequest(_rbuilderRpcModule, "rbuilder_getCodeByHash", theHash);

        response.Should().Contain(theCode);
    }

    [Test]
    public async Task Test_getCodeByHashNotFound()
    {
        string theCode = "01234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567";
        byte[] theCodeBytes = Bytes.FromHexString(theCode);
        Hash256 theHash = Keccak.Compute(theCodeBytes);
        string response = await RpcTest.TestSerializedRequest(_rbuilderRpcModule, "rbuilder_getCodeByHash", theHash);
        response.Should().Contain("null");
    }

    [Test]
    public async Task Test_calculateStateRoot()
    {
        Dictionary<Address, AccountChange> accountDiff = new Dictionary<Address, AccountChange>();
        accountDiff[TestItem.AddressA] = new AccountChange()
        {
            Nonce = 10,
            Balance = 20,
            SelfDestructed = true,
            ChangedSlots = new Dictionary<UInt256, UInt256>()
            {
                {0,0}
            }
        };

        string response = await RpcTest.TestSerializedRequest(_rbuilderRpcModule, "rbuilder_calculateStateRoot", "LATEST", accountDiff);
        response.Should().Contain("0x1df26ab740de451d16a6a902ccd0510943e6e70fae9739e65cf1aa16d8862a34");
    }

    [Test]
    public void test_bundle_ok_inner_tx_profits()
    {
        var caller = new Address("0x1fb09fa5326edc6eb54683657aa97a60d7a8d0ce");
        var to = new Address("0xfa1c5c79cf655b3d8cf94be2b697bf0449ecc03e");

        IWorldState worldState = _worldStateManager.GlobalWorldState;
        using var _ = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(caller, 1.Ether());
        worldState.CreateAccount(to, 1.Ether());
        worldState.Commit(London.Instance);
        worldState.CommitTree(0);
        _blockTree.FindLatestBlock()!.Header.StateRoot = worldState.StateRoot;

        var revmTransaction = new RevmTransaction
        {
            TxType = 2,
            Caller = caller,
            Kind = to,
            GasLimit = 1000000,
            GasPrice = 1,
            Value = 100000,
            Data = Bytes.FromHexString("0xf9da581d"),
            Nonce = 0,
            ChainId = 1,
            AccessList = AccessListForRpc.Empty,
            GasPriorityFee = 0,
            BlobHashes = [],
            MaxFeePerBlobGas = 0,
            AuthorizationList = AuthorizationListForRpc.Empty,
        };
        var bundleState = new BundleState();

        var wrapper = _rbuilderRpcModule.rbuilder_transact(revmTransaction, bundleState);
        wrapper.Result.Error.Should().BeNull();

        var revmExecutionResult = wrapper.Data.Result.Success;
        revmExecutionResult.GasUsed.Should().Be(28159);

        var revmState = wrapper.Data.State;
        revmState.Count.Should().Be(3);
        revmState[caller].Info.Balance.Should().Be(1.Ether() - 100000 - 28159);
        revmState[to].Info.Balance.Should().Be(1.Ether() + 100000);
    }
}
