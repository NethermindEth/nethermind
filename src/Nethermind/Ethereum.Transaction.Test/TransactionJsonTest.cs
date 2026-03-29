// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

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
    public void Convert_sets_AccessList_type_when_accessLists_field_present_but_empty()
    {
        const string json =
            """{"accessLists": [[]], "secretKey": "0x0000000000000000000000000000000000000000000000000000000000000001", "value": ["0x00"], "gasLimit": ["0x0186a0"], "data": ["0x"]}""";

        EthereumJsonSerializer serializer = new();
        TransactionJson txJson = serializer.Deserialize<TransactionJson>(json);

        Nethermind.Core.Transaction tx = JsonToEthereumTest.Convert(new PostStateJson { Indexes = new IndexesJson() }, txJson);

        tx.Type.Should().Be(TxType.AccessList,
            "presence of accessLists field (even empty) should set Type 1");
    }

    /// <summary>
    /// An AccessList transaction with an empty access list sent against Istanbul (pre-Berlin)
    /// must be rejected. The post-state root must equal the pre-state root - the invalid tx
    /// should not mutate state.
    /// Expected hash from pyspec: test_eip2930_tx_validity[fork_Istanbul-invalid-state_test]
    /// </summary>
    /// <remarks>
    /// NonParallelizable: this test builds a full Nethermind DI container. Running it
    /// concurrently with the ~170 parallel TransactionTests cases saturates the thread pool
    /// and deadlocks the sync-over-async container disposal, hanging the job for 15+ minutes.
    /// </remarks>
    [Test, NonParallelizable]
    public void Invalid_pre_berlin_access_list_tx_with_empty_list_preserves_prestate_root()
    {
        if (Environment.GetEnvironmentVariable("TEST_USE_FLAT") == "1")
            Assert.Ignore("Flat DB does not support pre-configured genesis state in this test setup");

        Address sender = new("0x1ad9bc24818784172ff393bb6f89f094d4d2ca29");
        Address recipient = new("0x67eb8fcbef83a0662b030f8bc89a10070c167a66");

        Nethermind.Core.Transaction transaction = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithChainId(1)
            .WithAccessList(AccessList.Empty)
            .WithGasLimit(100_000)
            .WithGasPrice(10)
            .WithNonce(UInt256.Zero)
            .To(recipient)
            .WithValue(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        // Override sender to match the pyspec fixture key
        transaction.SenderAddress = sender;

        GeneralStateTest test = new()
        {
            Name = nameof(Invalid_pre_berlin_access_list_tx_with_empty_list_preserves_prestate_root),
            Category = "state",
            Fork = Istanbul.Instance,
            ForkName = Istanbul.Instance.Name,
            CurrentCoinbase = new Address("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"),
            CurrentDifficulty = new UInt256(0x020000),
            CurrentGasLimit = 120_000_000,
            CurrentNumber = 1,
            CurrentTimestamp = 1000,
            PreviousHash = Keccak.Zero,
            Pre = new()
            {
                [recipient] = new()
                {
                    Nonce = UInt256.One,
                    Balance = UInt256.Zero,
                    Code = [0x60, 0x01, 0x60, 0x00, 0x55],  // PUSH1 1 PUSH1 0 SSTORE
                    Storage = new() { [UInt256.Zero] = new UInt256(0xdeadbeef).ToBigEndian() }
                },
                [sender] = new()
                {
                    Nonce = UInt256.Zero,
                    Balance = UInt256.Parse("1000000000000000000000"),
                    Code = [],
                    Storage = new()
                }
            },
            // Expected post-state root from pyspec fixture (pre-state unchanged)
            PostHash = new Hash256("0x43c19943b2c4a638fe07dbc954c1422032ea7c5e17d0d659f25a5324ed75f0be"),
            Transaction = transaction,
        };

        EthereumTestResult result = RunTest(test);

        result.StateRoot.Should().Be(test.PostHash,
            "invalid AccessList tx on pre-Berlin fork should not mutate state");
        result.Pass.Should().BeTrue();
    }

}
