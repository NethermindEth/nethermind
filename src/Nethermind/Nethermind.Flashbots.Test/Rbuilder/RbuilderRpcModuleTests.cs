// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Flashbots.Modules.Rbuilder;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.JsonRpc;
using Nethermind.State;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Flashbots.Test.Rbuilder;

public class RbuilderRpcModuleTests
{
    private IRbuilderRpcModule _rbuilderRpcModule;
    private IWorldStateManager _worldStateManager;
    private IContainer _container;

    [SetUp]
    public void Setup()
    {
        IBlockTree blockTree = Build.A.BlockTree()
            .OfChainLength(10)
            .TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new FlashbotsModule(new FlashbotsConfig(), new JsonRpcConfig()))
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .Build();

        _worldStateManager = _container.Resolve<IWorldStateManager>();
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
        using (worldState.BeginScope(null))
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
}
