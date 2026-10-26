// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    private static readonly byte[] StopCode = [(byte)Instruction.STOP];

    [TestCase("success", 21_000UL, TestName = "Execute_NativeSuccess_MetersGasWithoutFeesOrNonce")]
    [TestCase("revert", 21_004UL, TestName = "Execute_NativeRevert_RollsBackMintAndMetersGas")]
    [TestCase("halt", 16_777_216UL, TestName = "Execute_NativeHalt_RollsBackMintAndChargesFullBudget")]
    public void Execute_NativeEnvelope_MetersGasAndPreservesNonceAndFees(string outcome, ulong expectedGas) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            byte[] code = outcome switch
            {
                "revert" => [(byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT],
                "halt" => [(byte)Instruction.INVALID],
                _ => [(byte)Instruction.STOP],
            };
            state.CreateAccount(Address.SystemUser, 17, 8);
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.CreateAccount(TestItem.AddressC, 13);
            state.CreateAccount(TestItem.AddressD, 11);
            state.InsertCode(EezConstants.RollupXEezl2Address, code, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction tx = NativeTransaction(7, EezChainRules.RollupXSystemTxGasCap);
            CallOutputTracer tracer = new();
            TransactionResult result = processor.Execute(tx, tracer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(tracer.StatusCode, Is.EqualTo(outcome == "success" ? 1 : 0));
                Assert.That(state.GetNonce(Address.SystemUser), Is.EqualTo(8UL));
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo((UInt256)17));
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)(outcome == "success" ? 12 : 5)));
                Assert.That(state.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)13));
                Assert.That(state.GetBalance(TestItem.AddressD), Is.EqualTo((UInt256)11));
                Assert.That(header.GasUsed, Is.EqualTo(expectedGas));
            }
        });

    [Test]
    public void Execute_NativeWrongPositionGas_RejectsWithoutChangingReceivedBudget([Values] bool above) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            header.GasUsed = header.GasLimit - 100_000;
            Transaction tx = NativeTransaction(0, above ? 100_001UL : 99_999UL);
            ulong gas = tx.GasLimit;
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.False);
                Assert.That(tx.GasLimit, Is.EqualTo(gas));
                Assert.That(header.GasUsed, Is.EqualTo(header.GasLimit - 100_000));
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
            }
        });

    [Test]
    public void Execute_NativeExactRemainingGas_Accepts([Values] bool history, [Values] bool reducedBudget) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            ulong used = reducedBudget ? header.GasLimit - 100_000 : 0;
            header.GasUsed = used;
            ulong budget = reducedBudget ? 100_000 : history ? EezConstants.L1HistoryTxGasCap : EezChainRules.RollupXSystemTxGasCap;
            Transaction tx = NativeTransaction(0, budget, history ? EezConstants.L1HistoryAddress : null);
            state.CreateAccount(tx.To!, 0, 1);
            state.InsertCode(tx.To!, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(header.GasUsed, Is.EqualTo(used + 21_000));
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
            }
        });

    [Test]
    public void Execute_NativePositionBudgetBelowGasRequirement_Rejects([Values] bool belowIntrinsic) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            ulong budget = belowIntrinsic ? 1000UL : 200_000UL;
            header.GasUsed = header.GasLimit - budget;
            byte[] data = new byte[10_000];
            data.AsSpan().Fill(1);
            Transaction tx = NativeTransaction(0, budget, data: data);
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            TransactionResult.ErrorType expected = belowIntrinsic
                ? TransactionResult.ErrorType.GasLimitBelowIntrinsicGas
                : TransactionResult.ErrorType.GasLimitBelowFloorGas;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Error, Is.EqualTo(expected),
                    "the position-correct budget must still cover intrinsic gas and the calldata floor");
                Assert.That(header.GasUsed, Is.EqualTo(header.GasLimit - budget));
                Assert.That(state.AccountExists(Address.SystemUser), Is.False);
            }
        });

    [Test]
    public void Execute_NativeOnLegacyProfile_RejectsBeforeSystemCallDispatch([Values] bool rollupX)
    {
        EezChainRules rules = rollupX ? EezChainRules.RollupX(NativeSchedule()) : EezChainRules.Rollup0;
        WithProcessor(rules, (_, processor, header) =>
        {
            Transaction tx = NativeTransaction(0, EezChainRules.RollupXSystemTxGasCap);
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.False);
                Assert.That(header.GasUsed, Is.Zero);
            }
        });
    }

    [Test]
    public void Execute_NativeNonProtocolFields_RejectBeforeChangingState([Values("chain", "source", "sender", "nonce", "fees", "signature", "target", "type", "blobfee")] string field) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            Transaction tx = NativeTransaction(0, EezChainRules.RollupXSystemTxGasCap);
            switch (field)
            {
                case "chain": tx.ChainId = 2; break;
                case "source": tx.SourceHash = null; break;
                case "sender": tx.SenderAddress = EezConstants.SystemAddress; break;
                case "nonce": tx.Nonce = 1; break;
                case "fees": tx.GasPrice = 1; break;
                case "signature": tx.Signature = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject.Signature; break;
                case "target": tx.To = TestItem.AddressA; break;
                case "type": tx.Type = EezConstants.SystemTxType; break;
                case "blobfee": tx.MaxFeePerBlobGas = UInt256.Zero; break;
            }
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.False);
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
                Assert.That(header.GasUsed, Is.Zero);
            }
        });

    [Test]
    public void Execute_NativeMintOverflow_RejectsWithoutGasOrBalanceChanges() =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            state.CreateAccount(Address.SystemUser, 1, 8);
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction tx = NativeTransaction(UInt256.MaxValue, EezChainRules.RollupXSystemTxGasCap);
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.False);
                Assert.That(state.GetNonce(Address.SystemUser), Is.EqualTo(8UL));
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo(UInt256.One));
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)5));
                Assert.That(header.GasUsed, Is.Zero);
            }
        });

    [Test]
    public void Execute_NativeProfile_EthereumSystemCallRetainsItsUnmeteredNonceFreePath() =>
        WithProcessor(NativeRules(), (state, processor, _) =>
        {
            state.CreateAccount(TestItem.AddressB, 0, 1);
            state.InsertCode(TestItem.AddressB, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            SystemCall call = new() { SenderAddress = Address.SystemUser, To = TestItem.AddressB, GasLimit = 1000 };
            TransactionResult result = processor.Execute(call, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True,
                    "a protocol system call keeps the standard path even below transaction intrinsic gas");
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
            }
        });

    [Test]
    public void Execute_NativeProfile_OrdinaryTransactionsRetainNonceValueAndFees([Values] bool revert) =>
        WithProcessor(NativeRules(), (state, processor, header) =>
        {
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether, 5);
            state.CreateAccount(TestItem.AddressB, 5, 1);
            state.CreateAccount(TestItem.AddressC, 13);
            state.CreateAccount(TestItem.AddressD, 11);
            byte[] code = revert ? [(byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT] : StopCode;
            state.InsertCode(TestItem.AddressB, code, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction tx = Build.A.Transaction.WithChainId(1).WithNonce(5).WithTo(TestItem.AddressB).WithValue(7)
                .WithGasLimit(100_000).WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            TransactionResult result = processor.Execute(tx, new CallOutputTracer());
            ulong gas = revert ? 21_004UL : 21_000UL;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo(6UL));
                Assert.That(state.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(1.Ether - gas * 2.GWei - (revert ? UInt256.Zero : (UInt256)7)));
                Assert.That(state.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)(revert ? 5 : 12)));
                Assert.That(state.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)13 + gas * 1.GWei));
                Assert.That(state.GetBalance(TestItem.AddressD), Is.EqualTo((UInt256)11 + gas * 1.GWei));
            }
        });

    [Test]
    public void CallAndRestore_NativeEnvelope_DoesNotDecrementSystemNonce() =>
        WithProcessor(NativeRules(), (state, processor, _) =>
        {
            state.CreateAccount(Address.SystemUser, 17, 8);
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction tx = NativeTransaction(7, EezChainRules.RollupXSystemTxGasCap);
            TransactionResult result = processor.CallAndRestore(tx, new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(state.GetNonce(Address.SystemUser), Is.EqualTo(8UL));
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo((UInt256)17));
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)5));
            }
        });

    [Test]
    public void Rules_NativeCopy_PreservesWireProfileAndCleanupAddresses()
    {
        EezChainRules rules = NativeRules().WithoutConsensusLayerCalls();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.SystemTransactionType, Is.EqualTo(EezConstants.NativeSystemTxType));
            Assert.That(rules.BurnedAtBlockStart, Is.EquivalentTo(new[] { Address.SystemUser, EezConstants.RollupXEezl2Address }));
            Assert.That(rules.FeeCollector, Is.EqualTo(TestItem.AddressD));
        }
    }

    [Test]
    public void Rules_NativeReservedCollector_Rejects([Values("zero", "system", "manager")] string address)
    {
        Address collector = address switch
        {
            "system" => Address.SystemUser,
            "manager" => EezConstants.RollupXEezl2Address,
            _ => Address.Zero,
        };
        Assert.That(() => EezChainRules.NativeRollupX(NativeSchedule(), collector), Throws.TypeOf<ArgumentException>());
    }

    private static RollupXSchedule NativeSchedule() => new(2, 6, new RollupXL1Reference(1, 10, TestItem.KeccakA, 100, TestItem.KeccakB), 100);
    private static EezChainRules NativeRules() => EezChainRules.NativeRollupX(NativeSchedule(), TestItem.AddressD);

    private static void WithProcessor(EezChainRules rules, Action<IWorldState, ITransactionProcessor, BlockHeader> action)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance) { ChainId = 1 })
            .AddModule(new EezModule(new EezConfig(), rules))
            .Build();
        using ILifetimeScope scope = container.BeginLifetimeScope();
        IWorldState state = scope.Resolve<IWorldState>();
        using IDisposable stateScope = state.BeginScope(IWorldState.PreGenesis);
        ITransactionProcessor processor = scope.Resolve<ITransactionProcessor>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithGasLimit(30_000_000)
            .WithBeneficiary(TestItem.AddressC).TestObject;
        header.IsPostMerge = true;
        header.BaseFeePerGas = 1.GWei;
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, scope.Resolve<ISpecProvider>().GetSpec(header)));
        action(state, processor, header);
    }

    private static Transaction NativeTransaction(UInt256 value, ulong gas, Address? to = null, ReadOnlyMemory<byte> data = default)
    {
        Transaction tx = new()
        {
            Type = EezConstants.NativeSystemTxType, ChainId = 1, SourceHash = TestItem.KeccakC,
            SenderAddress = Address.SystemUser, To = to ?? EezConstants.RollupXEezl2Address, Value = value, GasLimit = gas, Data = data,
        };
        EezNativeSystemTxDecoder decoder = new();
        byte[] encoded = new byte[decoder.GetLength(tx, RlpBehaviors.SkipTypedWrapping)];
        RlpWriter writer = new(encoded);
        decoder.Encode(tx, ref writer, RlpBehaviors.SkipTypedWrapping);
        tx.Hash = Keccak.Compute(encoded);
        return tx;
    }

}
