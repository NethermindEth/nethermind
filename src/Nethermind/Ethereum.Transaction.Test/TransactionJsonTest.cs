// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
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
        Assert.That(txJson.AccessLists, Is.Not.Null);
        Assert.That(txJson.AccessLists[0][0].Address, Is.EqualTo(new Address("0x0001020304050607080900010203040506070809")));
        Assert.That(txJson.AccessLists[0][0].StorageKeys[1][0], Is.EqualTo((byte)1));

        Nethermind.Core.Transaction tx = JsonToEthereumTest.Convert(new PostStateJson { Indexes = new IndexesJson() }, txJson);
        Assert.That(tx.AccessList, Is.Not.Null);
    }

    [Test]
    public void Convert_sets_AccessList_type_when_accessLists_field_present_but_empty()
    {
        const string json =
            """{"accessLists": [[]], "secretKey": "0x0000000000000000000000000000000000000000000000000000000000000001", "value": ["0x00"], "gasLimit": ["0x0186a0"], "data": ["0x"]}""";

        EthereumJsonSerializer serializer = new();
        TransactionJson txJson = serializer.Deserialize<TransactionJson>(json);

        Nethermind.Core.Transaction tx = JsonToEthereumTest.Convert(new PostStateJson { Indexes = new IndexesJson() }, txJson);

        Assert.That(tx.Type, Is.EqualTo(TxType.AccessList), "presence of accessLists field (even empty) should set Type 1");
    }

    [Test]
    public void Amsterdam_state_test_without_env_slot_number_defaults_to_zero()
    {
        Address contract = new("0x0000000000000000000000707690000000008024");
        using PrivateKey senderKey = new("0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8");
        Nethermind.Core.Transaction transaction = Build.A.Transaction
            .WithChainId(1)
            .WithGasPrice(0x10)
            .WithGasLimit(0x100000)
            .WithNonce(UInt256.Zero)
            .To(contract)
            .WithValue(0)
            .SignedAndResolved(senderKey)
            .TestObject;

        GeneralStateTest test = new()
        {
            Name = nameof(Amsterdam_state_test_without_env_slot_number_defaults_to_zero),
            Category = "state",
            Fork = Amsterdam.Instance,
            ForkName = Amsterdam.Instance.Name,
            CurrentCoinbase = new Address("0xb94f5374fce5edbc8e2a8697c15331677e6ebf0b"),
            CurrentDifficulty = new UInt256(0x200000),
            CurrentGasLimit = 0x26e1f476fe1e22,
            CurrentNumber = 1,
            CurrentTimestamp = 1000,
            CurrentBaseFee = 0x10,
            CurrentRandom = new Hash256("0x0000000000000000000000000000000000000000000000000000000000200000"),
            PreviousHash = new Hash256("0x044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d"),
            Pre = new()
            {
                [contract] = new()
                {
                    Code = Bytes.FromHexString("0x4b600055"),
                    Balance = 1_000_000_000,
                },
                [senderKey.Address] = new()
                {
                    Balance = UInt256.Parse("0xffffffffff"),
                }
            },
            PostHash = new Hash256("0x7b8e9fcbf409db592f7263787cb6440e5a0b534efd3dd92e9b287dda0a84c080"),
            Transaction = transaction,
        };

        EthereumTestResult result = RunTest(test);

        Assert.That(result.Pass, Is.True);
        Assert.That(result.StateRoot, Is.EqualTo(test.PostHash));
    }

    /// <summary>
    /// An AccessList transaction with an empty access list sent against Istanbul (pre-Berlin)
    /// must be rejected. The post-state root must equal the pre-state root - the invalid tx
    /// should not mutate state.
    /// Expected hash from pyspec: test_eip2930_tx_validity[fork_Istanbul-invalid-state_test]
    /// </summary>
    [Test]
    public void Invalid_pre_berlin_access_list_tx_with_empty_list_preserves_prestate_root()
    {
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
                    Storage = []
                }
            },
            // Expected post-state root from pyspec fixture (pre-state unchanged)
            PostHash = new Hash256("0x43c19943b2c4a638fe07dbc954c1422032ea7c5e17d0d659f25a5324ed75f0be"),
            Transaction = transaction,
        };

        EthereumTestResult result = RunTest(test);

        Assert.That(result.StateRoot, Is.EqualTo(test.PostHash), "invalid AccessList tx on pre-Berlin fork should not mutate state");
        Assert.That(result.Pass, Is.True);
    }

}
