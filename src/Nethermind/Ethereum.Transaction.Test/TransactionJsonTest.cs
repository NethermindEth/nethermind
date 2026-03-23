// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using FluentAssertions;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Consensus.Processing;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Nethermind.Core.Extensions;

namespace Ethereum.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TransactionJsonTest : GeneralStateTestBase
{
    [Test]
    public void Can_load_access_lists()
    {
        const string lists =
            "{\"accessLists\": [[{\"address\": \"0x0001020304050607080900010203040506070809\", \"storageKeys\": [\"0x00\", \"0x01\"]}]]}";

        EthereumJsonSerializer serializer = new();
        TransactionJson txJson = serializer.Deserialize<TransactionJson>(lists);
        txJson.SecretKey = TestItem.PrivateKeyA.KeyBytes;
        txJson.Value = new UInt256[1];
        txJson.GasLimit = new long[1];
        txJson.Data = new byte[1][];
        txJson.AccessLists.Should().NotBeNull();
        txJson.AccessLists[0][0].Address.Should()
            .BeEquivalentTo(new Address("0x0001020304050607080900010203040506070809"));
        txJson.AccessLists[0][0].StorageKeys[1][0].Should().Be((byte)1);

        Nethermind.Core.Transaction tx = JsonToEthereumTest.Convert(new PostStateJson { Indexes = new IndexesJson() }, txJson);
        tx.AccessList.Should().NotBeNull();
    }

    [Test]
    public void Invalid_pre_berlin_access_list_tx_preserves_prestate_root()
    {
        Dictionary<Address, AccountState> preState = new()
        {
            [TestItem.AddressA] = new() { Balance = 100.Ether, Nonce = UInt256.Zero },
            [TestItem.AddressB] = new() { Balance = UInt256.Zero, Nonce = UInt256.Zero },
        };

        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressB)
            .AddStorage(UInt256.One)
            .Build();

        Nethermind.Core.Transaction transaction = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithChainId(MainnetSpecProvider.Instance.ChainId)
            .WithAccessList(accessList)
            .WithGasLimit(50_000)
            .WithGasPrice(1)
            .WithNonce(UInt256.Zero)
            .To(TestItem.AddressB)
            .WithValue(1)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        GeneralStateTest test = new()
        {
            Name = nameof(Invalid_pre_berlin_access_list_tx_preserves_prestate_root),
            Category = "state",
            Fork = Istanbul.Instance,
            ForkName = Istanbul.Instance.Name,
            CurrentCoinbase = TestItem.AddressC,
            CurrentDifficulty = UInt256.One,
            CurrentGasLimit = 1_000_000,
            CurrentNumber = 1,
            CurrentTimestamp = 0,
            PreviousHash = Keccak.Zero,
            Pre = preState,
            PostHash = CalculatePreStateRoot(preState),
            Transaction = transaction,
        };

        RunTest(test).Pass.Should().BeTrue();
    }

    private static Hash256 CalculatePreStateRoot(Dictionary<Address, AccountState> preState)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<IBlockhashProvider>(new TestBlockhashProvider())
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .AddSingleton(LimboLogs.Instance)
            .Build();

        IMainProcessingContext mainProcessingContext = container.Resolve<IMainProcessingContext>();
        IWorldState stateProvider = mainProcessingContext.WorldState;
        using IDisposable _ = stateProvider.BeginScope(null);

        InitializeTestState(preState, stateProvider, MainnetSpecProvider.Instance);
        return stateProvider.StateRoot;
    }
}
