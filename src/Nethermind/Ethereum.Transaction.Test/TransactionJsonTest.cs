// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class TransactionJsonTest : GeneralStateTestBase
{
    private static readonly Address Recipient = new("0x67eb8fcbef83a0662b030f8bc89a10070c167a66");
    private static readonly Address Coinbase = new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba");
    private static readonly UInt256 SenderBalance = 1_000_000;
    private static readonly UInt256 RecipientBalance = 1;
    private static readonly UInt256 CoinbaseBalance = 1;
    private static readonly UInt256 TransferredValue = 1_000;

    /// <summary>
    /// In range for every signature validator - r and s are non-zero and below the curve order, and
    /// v is 27 - yet no public key recovers from it.
    /// </summary>
    private static readonly Signature UnrecoverableSignature = new(5, 1, 27);

    [Test]
    public void Can_load_access_lists()
    {
        const string lists =
            "{\"accessLists\": [[{\"address\": \"0x0001020304050607080900010203040506070809\", \"storageKeys\": [\"0x00\", \"0x01\"]}]]}";

        EthereumJsonSerializer serializer = new();
        TransactionJson txJson = serializer.Deserialize<TransactionJson>(lists);
        txJson.SecretKey = TestItem.PrivateKeyA.KeyBytes;
        txJson.Value = new UInt256[1];
        txJson.GasLimit = new ulong[1];
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
            .WithNonce(0UL)
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
            PostHash = new Hash256("0xbb8e5ab8df3709e0abf2d53bb3bdbbea8159d35ea16d2c7fb5921fbc3de31ef6"),
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
            .WithNonce(0UL)
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
                    Nonce = 1UL,
                    Balance = UInt256.Zero,
                    Code = [0x60, 0x01, 0x60, 0x00, 0x55],  // PUSH1 1 PUSH1 0 SSTORE
                    Storage = new() { [UInt256.Zero] = new UInt256(0xdeadbeef).ToBigEndian() }
                },
                [sender] = new()
                {
                    Nonce = 0UL,
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

    /// <summary>
    /// A state-test fixture names its sender, but the account that executes has to be the one the
    /// fixture's own signature recovers to. An in-range yet unrecoverable signature recovers to
    /// nobody, so the transaction must be rejected rather than run as the funded account the fixture
    /// names: no value moves and the sender's nonce stays put.
    /// </summary>
    /// <remarks>
    /// The recoverable case is the control - the same fixture shape does transfer and does advance the
    /// nonce, so the rejection is attributable to the signature alone. Mirrors upstream
    /// <c>frontier/validation/test_transaction.py::test_unrecoverable_signature</c>.
    /// </remarks>
    [Test]
    public void Fixture_transaction_executes_only_as_the_sender_its_signature_recovers([Values] bool signatureRecovers)
    {
        using PrivateKey senderKey = new("0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8");

        Nethermind.Core.Transaction pinned = new()
        {
            Nonce = 0,
            GasPrice = UInt256.Zero,
            GasLimit = GasCostOf.Transaction,
            To = Recipient,
            Value = TransferredValue,
        };

        if (signatureRecovers)
        {
            new EthereumEcdsa(BlockchainIds.Mainnet).Sign(senderKey, pinned, isEip155Enabled: false);
        }
        else
        {
            pinned.Signature = UnrecoverableSignature;
        }

        PostStateJson postStateJson = new()
        {
            ExpectException = "TransactionException.INVALID_SIGNATURE_VRS",
            Txbytes = Rlp.Encode(pinned, RlpBehaviors.SkipTypedWrapping).Bytes,
        };
        TransactionJson transactionJson = new() { Sender = senderKey.Address };

        Dictionary<Address, AccountState> pre = Accounts(senderKey.Address, SenderBalance, senderNonce: 0, RecipientBalance);
        Dictionary<Address, AccountState> expectedPost = signatureRecovers
            ? Accounts(senderKey.Address, SenderBalance - TransferredValue, senderNonce: 1, RecipientBalance + TransferredValue)
            : pre;

        GeneralStateTest test = new()
        {
            Name = nameof(Fixture_transaction_executes_only_as_the_sender_its_signature_recovers),
            Category = "state",
            Fork = Frontier.Instance,
            ForkName = Frontier.Instance.Name,
            CurrentCoinbase = Coinbase,
            CurrentDifficulty = new UInt256(0x020000),
            CurrentGasLimit = 1_000_000,
            CurrentNumber = 1,
            CurrentTimestamp = 1000,
            PreviousHash = Keccak.Zero,
            Pre = pre,
            PostHash = StateRootOf(expectedPost),
            Transaction = JsonToEthereumTest.Convert(postStateJson, transactionJson, BlockchainIds.Mainnet),
        };

        EthereumTestResult result = RunTest(test);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(test.PostHash),
                "the signature, not the fixture's named sender, decides whether value moves and the nonce advances");
            Assert.That(result.Error,
                Is.EqualTo(signatureRecovers ? null : TransactionResult.SenderNotSpecified.ErrorDescription),
                "an unrecoverable signature leaves the transaction with no sender to execute as");
        }
    }

    private static Dictionary<Address, AccountState> Accounts(Address sender, UInt256 senderBalance, ulong senderNonce, UInt256 recipientBalance) => new()
    {
        [sender] = new() { Balance = senderBalance, Nonce = senderNonce },
        [Recipient] = new() { Balance = recipientBalance },
        [Coinbase] = new() { Balance = CoinbaseBalance },
    };

    private static Hash256 StateRootOf(Dictionary<Address, AccountState> accounts)
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(null);
        InitializeTestState(accounts, worldState, MainnetSpecProvider.Instance);
        return worldState.StateRoot;
    }
}
