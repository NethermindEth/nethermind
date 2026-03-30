// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Ethereum.Test.Base;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test;

/// <summary>
/// Uses ParallelScope.Self (not All) so that the fixture can run alongside TransactionTests
/// but its own tests run sequentially. ParallelScope.All caused NUnit worker starvation
/// when the KZG-initialising test competed with 171 parallel RLP tests in checked builds.
/// Does not inherit GeneralStateTestBase to avoid triggering its static constructor
/// (KZG init) during NUnit discovery while the thread pool is saturated.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class TransactionJsonTest
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
    /// must be rejected. The post-state root must equal the pre-state root — the invalid tx
    /// should not mutate state.
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
                    Code = [0x60, 0x01, 0x60, 0x00, 0x55],
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
            PostHash = new Hash256("0x43c19943b2c4a638fe07dbc954c1422032ea7c5e17d0d659f25a5324ed75f0be"),
            Transaction = transaction,
        };

        KzgPolynomialCommitments.Initialize();
        EthereumTestResult result = RunStateTest(test);

        result.StateRoot.Should().Be(test.PostHash,
            "invalid AccessList tx on pre-Berlin fork should not mutate state");
        result.Pass.Should().BeTrue();
    }

    private static EthereumTestResult RunStateTest(GeneralStateTest test)
    {
        test.Fork = ChainUtils.ResolveSpec(test.Fork, test.ChainId);

        ISpecProvider specProvider =
            new CustomSpecProvider(test.ChainId, test.ChainId,
                ((ForkActivation)0, test.GenesisSpec),
                ((ForkActivation)1, test.Fork));

        IConfigProvider configProvider = new ConfigProvider();
        ILogManager logManager = LimboLogs.Instance;

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton<IBlockhashProvider>(new TestBlockhashProvider())
            .AddSingleton(specProvider)
            .AddSingleton(logManager)
            .Build();

        IMainProcessingContext ctx = container.Resolve<IMainProcessingContext>();
        IWorldState stateProvider = ctx.WorldState;
        using System.IDisposable scope = stateProvider.BeginScope(null);
        IBlockValidator blockValidator = container.Resolve<IBlockValidator>();
        ITransactionProcessor transactionProcessor = ctx.TransactionProcessor;

        foreach (KeyValuePair<Address, AccountState> accountState in test.Pre)
        {
            foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
            {
                stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key),
                    storageItem.Value.WithoutLeadingZeros().ToArray());
            }

            stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance, accountState.Value.Nonce);
            stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
        }

        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);
        stateProvider.Reset();

        Snapshot preExecutionSnapshot = stateProvider.TakeSnapshot(newTransactionStart: true);
        test.Transaction.ChainId ??= test.ChainId;

        IReleaseSpec spec = specProvider.GetSpec((ForkActivation)test.CurrentNumber);
        Nethermind.Core.Transaction[] transactions = [test.Transaction];
        Withdrawal[] withdrawals = spec.WithdrawalsEnabled ? [] : null;

        BlockHeader header = new(
            test.PreviousHash,
            Keccak.OfAnEmptySequenceRlp,
            test.CurrentCoinbase,
            test.CurrentDifficulty,
            test.CurrentNumber,
            test.CurrentGasLimit,
            test.CurrentTimestamp,
            [])
        {
            BaseFeePerGas = UInt256.Zero,
            StateRoot = test.PostHash,
            IsPostMerge = test.CurrentRandom is not null,
            MixHash = test.CurrentRandom,
            WithdrawalsRoot = test.CurrentWithdrawalsRoot ?? (spec.WithdrawalsEnabled ? PatriciaTree.EmptyTreeHash : null),
            ParentBeaconBlockRoot = test.CurrentBeaconRoot,
            ExcessBlobGas = test.CurrentExcessBlobGas ?? (test.Fork.IsEip4844Enabled ? 0ul : null),
            SlotNumber = test.CurrentSlotNumber,
            BlobGasUsed = BlobGasCalculator.CalculateBlobGas(test.Transaction),
            RequestsHash = test.RequestsHash,
            BlockAccessListHash = spec.IsEip7928Enabled ? Keccak.OfAnEmptySequenceRlp : null,
            TxRoot = TxTrie.CalculateRoot(transactions),
            ReceiptsRoot = test.PostReceiptsRoot,
        };

        header.Hash = header.CalculateHash();
        Block block = new(header, new BlockBody(transactions, [], withdrawals));

        TransactionResult? txResult = null;

        if (blockValidator.ValidateOrphanedBlock(block, out _))
        {
            txResult = transactionProcessor.Execute(test.Transaction, new BlockExecutionContext(header, spec), NullTxTracer.Instance);
        }

        if (txResult is not null && txResult.Value == TransactionResult.Ok)
        {
            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
            stateProvider.CommitTree(1);
            stateProvider.CreateAccountIfNotExists(test.CurrentCoinbase, UInt256.Zero);
            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
            stateProvider.RecalculateStateRoot();
        }
        else
        {
            stateProvider.Restore(preExecutionSnapshot);
            stateProvider.RecalculateStateRoot();
        }

        bool pass = test.PostHash == stateProvider.StateRoot;
        return new EthereumTestResult(test.Name, test.ForkName, pass)
        {
            StateRoot = stateProvider.StateRoot
        };
    }
}
