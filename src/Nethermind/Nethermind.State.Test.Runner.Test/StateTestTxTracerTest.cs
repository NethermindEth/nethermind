// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

public class StateTestTxTracerTest : VirtualMachineTestsBase
{
    private StateTestTxTracer tracer;

    [SetUp]
    public void StateTestTxTracerSetUp() => tracer = new StateTestTxTracer();

    [TearDown]
    public void StateTestTxTracerTearDown() => tracer.Dispose();

    [Test]
    public void Does_not_throw_on_call()
    {
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 50000, 1000000.Ether)
            .Done;

        Assert.DoesNotThrow(() => Execute(tracer, code));
    }

    [Test]
    public void Does_not_throw_on_self_destruct()
    {
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        Assert.DoesNotThrow(() => Execute(tracer, code));
    }

    [Test]
    public void Reports_top_level_action_gas()
    {
        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(60, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        tracer.ReportActionEnd(40, default);
        tracer.ReportActionEnd(70, default);

        GasConsumed settledGas = new(SpentGas: 200, OperationGas: 180, BlockStateGas: 80, GasRefund: 20);
        tracer.MarkAsSuccess(Address.Zero, in settledGas, [], []);

        Assert.That(tracer.BuildResult().Result.GasUsed, Is.EqualTo(30));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Reports_zero_for_stop_call_with_calldata_floor(bool amsterdam)
    {
        Hash256 postHash = new(amsterdam
            ? "0x53a1c1658d4a73b0b3696812f30e2b247e3bd3fd289c9436979f14e3c23810be"
            : "0x9efbc3518d97c09664295c8fcf82ddc73ea94a770bfbd59bb98f4c2c6c8219a4");
        GeneralStateTest test = CreateStateTest(amsterdam ? Amsterdam.Instance : Osaka.Instance, [0], [0], 5_000_000, postHash);

        AssertTraceGas(test, 0);
    }

    [TestCase(20_000_000UL, 0x2f49UL)]
    [TestCase(200_000UL, 0x1adc9UL)]
    public void Reports_state_gas_only_when_it_spills_into_execution(ulong gasLimit, ulong expectedGasUsed)
    {
        Hash256 postHash = new("0xacd480565b8de9ee8f4c137da3d5d6ca7fdd70808d65abb33609a907c3339e41");
        GeneralStateTest test = CreateStateTest(Amsterdam.Instance, [0x60, 0x01, 0x5f, 0x55, 0x00], [], gasLimit, postHash);

        AssertTraceGas(test, expectedGasUsed);
    }

    [Test]
    public void Reports_refunded_state_gas_on_revert()
    {
        Hash256 postHash = new("0x0cdcee5f7be607fbf231de46ab3788ca0204fb402880036d85c2a1f7cf85cc84");
        GeneralStateTest test = CreateStateTest(Amsterdam.Instance, [0x60, 0x01, 0x5f, 0x55, 0x60, 0x00, 0x60, 0x00, 0xfd], [], 200_000, postHash);

        AssertTraceGas(test, 0x2f4f);
    }

    [Test]
    public void Does_not_charge_code_deposit_for_revert_data()
    {
        Hash256 postHash = new("0x157a9a369824ebd3e66e2658150f54254bcaf26e23e11163b6a1f39cd1e8b046");
        GeneralStateTest test = CreateStateTest(Osaka.Instance, [], [0x60, 0x01, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xfd], 100_000, postHash, contractCreation: true);

        AssertTraceGas(test, 0x12);
    }

    [Test]
    public void Falls_back_to_receipt_gas_for_create_collision()
    {
        Hash256 postHash = new("0x23f9dffa595df45c4c8bed92dfb495e14ebbc4ca6bcd3d1e63983da7ef1c4306");
        GeneralStateTest test = CreateStateTest(Osaka.Instance, [], [0x60, 0x00, 0x60, 0x00, 0xf3], 100_000, postHash, contractCreation: true, collision: true);
        tracer = new StateTestTxTracer(IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard);

        AssertTraceGas(test, 0xb75e);
    }

    private void AssertTraceGas(GeneralStateTest test, ulong expectedGasUsed)
    {
        EthereumTestResult result = new StateTestExecutor().Execute(test, tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.True, result.Error);
            Assert.That(tracer.BuildResult().Result.GasUsed, Is.EqualTo(expectedGasUsed));
        }
    }

    private static GeneralStateTest CreateStateTest(
        IReleaseSpec fork,
        byte[] code,
        byte[] data,
        ulong gasLimit,
        Hash256 postHash,
        bool contractCreation = false,
        bool collision = false)
    {
        Address recipient = TestItem.AddressB;
        TransactionBuilder<Transaction> transactionBuilder = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithChainId(1)
            .WithAccessList(AccessList.Empty)
            .WithData(data)
            .WithGasLimit(gasLimit)
            .WithGasPrice(7)
            .WithNonce(0)
            .WithValue(0);

        if (contractCreation)
            transactionBuilder.WithCode(data);
        else
            transactionBuilder.To(recipient);

        Transaction transaction = transactionBuilder
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        GeneralStateTest test = new()
        {
            Name = nameof(StateTestTxTracerTest),
            Category = "state",
            Fork = fork,
            CurrentCoinbase = TestItem.AddressC,
            CurrentDifficulty = UInt256.Zero,
            CurrentGasLimit = 100_000_000,
            CurrentNumber = 1,
            CurrentTimestamp = 1_000,
            CurrentBaseFee = 7,
            CurrentRandom = Hash256.Zero,
            CurrentExcessBlobGas = 0,
            PreviousHash = Hash256.Zero,
            Pre = new()
            {
                [transaction.SenderAddress!] = new AccountState { Balance = 1_000_000_000 },
            },
            PostHash = postHash,
            Transaction = transaction,
        };

        if (!contractCreation)
        {
            test.Pre[recipient] = new AccountState { Code = code, Nonce = 1 };
        }
        else if (collision)
        {
            Address deploymentAddress = ContractAddress.From(transaction.SenderAddress, transaction.Nonce);
            test.Pre[deploymentAddress] = new AccountState { Code = [0], Nonce = 1 };
        }

        return test;
    }

    private sealed class StateTestExecutor : GeneralStateTestBase
    {
        public EthereumTestResult Execute(GeneralStateTest test, ITxTracer txTracer) => RunTest(test, txTracer);
    }
}
