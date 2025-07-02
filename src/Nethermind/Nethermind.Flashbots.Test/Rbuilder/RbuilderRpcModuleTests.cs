// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Flashbots.Modules.Rbuilder;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Flashbots.Test.Rbuilder;

public class RbuilderRpcModuleTests
{
    private IRbuilderRpcModule _rbuilderRpcModule;
    private WorldStateManager _worldStateManager;

    [SetUp]
    public async Task Setup()
    {
        _worldStateManager = TestWorldStateFactory.CreateForTest(await TestMemDbProvider.InitAsync(), LimboLogs.Instance);
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
