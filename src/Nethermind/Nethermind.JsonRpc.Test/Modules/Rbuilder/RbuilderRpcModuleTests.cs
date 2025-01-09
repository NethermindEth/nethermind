// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.RBuilder;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.JsonRpc.Test.Modules.Rbuilder;

public class RbuilderRpcModuleTests
{
    private IRbuilderRpcModule _rbuilderRpcModule;
    private WorldStateManager _worldStateManager;

    [SetUp]
    public async Task Setup()
    {
        _worldStateManager = WorldStateManager.CreateForTest(await TestMemDbProvider.InitAsync(), LimboLogs.Instance);
        IBlockTree blockTree = Build.A.BlockTree()
            .OfChainLength(10)
            .TestObject;

        _rbuilderRpcModule = new RbuilderRpcModule(blockTree, MainnetSpecProvider.Instance, _worldStateManager);
    }

    [Test]
    public async Task Test_getCodeByHash()
    {
        string theCode = "01234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567012345670123456701234567";
        byte[] theCodeBytes = Bytes.FromHexString(theCode);
        Hash256 theHash = Keccak.Compute(theCodeBytes);
        Console.Error.WriteLine(theHash.ToString());

        IWorldState worldState = _worldStateManager.GlobalWorldState;
        worldState.CreateAccount(TestItem.AddressA, 100000);
        worldState.InsertCode(TestItem.AddressA, theCodeBytes, London.Instance);
        worldState.Commit(London.Instance);
        worldState.CommitTree(0);

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
            Code = [1, 2, 3, 4],
            SelfDestructed = true,
            ChangedSlots = new Dictionary<Hash256, Hash256>()
            {
                { TestItem.KeccakA, TestItem.KeccakB }
            }
        };

        string response = await RpcTest.TestSerializedRequest(_rbuilderRpcModule, "rbuilder_calculateStateRoot", "LATEST", accountDiff);
        response.Should().Contain("0x4dab99008d5b6a24037cf0b601adf7526af44e89c1cef06ccf220b07c497bcd5");
    }
}
