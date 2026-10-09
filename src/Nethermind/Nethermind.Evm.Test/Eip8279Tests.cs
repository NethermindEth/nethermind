// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8279: block access list byte floor.
/// </summary>
/// <remarks>
/// Each transaction carries enough calldata for the floor to bind, so the gas it spends is exactly the static floor
/// plus 64 gas per block access list byte metered during execution.
/// </remarks>
public class Eip8279Tests : VirtualMachineTestsBase
{
    private const int FloorBindingCalldataBytes = 20_000;
    private const ulong GasLimit = 2_000_000;

    // EIP-8298 is on in both so the SETCODEFROM cases run with and without EIP-8279.
    private static readonly IReleaseSpec Spec8279 = new OverridableReleaseSpec(Bogota.Instance) { IsEip8131Enabled = true, IsEip8279Enabled = true, IsEip8298Enabled = true };
    private static readonly IReleaseSpec Spec8131Only = new OverridableReleaseSpec(Bogota.Instance) { IsEip8131Enabled = true, IsEip8298Enabled = true };
    private static readonly Address Executing = TestItem.AddressB;
    private static readonly Address ColdAccount = TestItem.AddressC;
    private static readonly Address Callee = TestItem.AddressE;
    private static readonly Address RevertingWriter = TestItem.AddressF;
    private static readonly Address RevertingRestorer = new("0x00000000000000000000000000000000000c0de5");
    private static readonly Address StarvedWriter = new("0x00000000000000000000000000000000000c0de6");
    private static readonly Address StarvedCopier = new("0x00000000000000000000000000000000000c0de7");
    private static readonly Address Precompile = new("0x0000000000000000000000000000000000000004");
    private static readonly Address CodeSource = new("0x00000000000000000000000000000000000c0de8");
    private static readonly byte[] AdoptedCode = [1, 2, 3, 4, 5, 6, 7];

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(Spec8279);

    private static Prepare SStore(int key, int value) => Prepare.EvmCode.PushData(value).PushData(key).Op(Instruction.SSTORE);

    private static IEnumerable<TestCaseData> MeteredBytesCases()
    {
        yield return Case("Cold BALANCE", Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.BALANCE), 20);
        yield return Case("Repeated BALANCE meters once", Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.BALANCE).PushData(ColdAccount).Op(Instruction.BALANCE), 20);
        // First touch is tracked per transaction, not by warmth: pre-warmed accounts still meter.
        yield return Case("BALANCE of a precompile", Prepare.EvmCode.PushData(Precompile).Op(Instruction.BALANCE), 20);
        yield return Case("BALANCE of the executing account", Prepare.EvmCode.PushData(Executing).Op(Instruction.BALANCE), 20);
        yield return Case("Cold EXTCODESIZE", Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.EXTCODESIZE), 20);
        yield return Case("Cold EXTCODEHASH", Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.EXTCODEHASH), 20);
        yield return Case("Cold EXTCODECOPY", Prepare.EvmCode.PushData(32).PushData(0).PushData(0).PushData(ColdAccount).Op(Instruction.EXTCODECOPY), 20);
        yield return Case("Empty EXTCODECOPY", Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(ColdAccount).Op(Instruction.EXTCODECOPY), 20);
        yield return Case("Cold SLOAD", Prepare.EvmCode.PushData(1).Op(Instruction.SLOAD), 32);
        yield return Case("Repeated SLOAD meters once", Prepare.EvmCode.PushData(1).Op(Instruction.SLOAD).PushData(1).Op(Instruction.SLOAD), 32);
        yield return Case("Cold SSTORE zero to non-zero", SStore(2, 1), 64);
        yield return Case("SSTORE of the original value", SStore(1, 5), 32);
        yield return Case("Net-zero SSTORE round trip gives nothing back", SStore(1, 7).PushData(5).PushData(1).Op(Instruction.SSTORE), 64);
        yield return Case("Repeated distinct writes meter the value once", SStore(1, 7).PushData(8).PushData(1).Op(Instruction.SSTORE).PushData(9).PushData(1).Op(Instruction.SSTORE), 64);
        yield return Case("Leaving the original value again re-meters the value", SStore(1, 7).PushData(5).PushData(1).Op(Instruction.SSTORE).PushData(8).PushData(1).Op(Instruction.SSTORE), 96);
        yield return Case("Clearing a slot keeps its value bytes", SStore(1, 0), 64);
        yield return Case("CALL without value", Prepare.EvmCode.Call(ColdAccount, 50_000), 20);
        yield return Case("CALL with value", Prepare.EvmCode.CallWithValue(ColdAccount, 50_000, 1), 20 + 64);
        yield return Case("CALL with value to a precompile", Prepare.EvmCode.CallWithValue(Precompile, 50_000, 1), 20 + 64);
        yield return Case("CALLCODE with value adds no balance bytes", Prepare.EvmCode.CallCode(ColdAccount, 50_000, 1), 20);
        yield return Case("DELEGATECALL", Prepare.EvmCode.DelegateCall(ColdAccount, 50_000), 20);
        yield return Case("STATICCALL", Prepare.EvmCode.StaticCall(ColdAccount, 50_000), 20);
        yield return Case("Reverted CALL with value is not rewound", Prepare.EvmCode.CallWithValue(Callee, 50_000, 1), 20 + 64 + 32);
        yield return Case("SLOAD in a reverted frame is not rewound", Prepare.EvmCode.Call(Callee, 50_000), 20 + 32);
        // The reverted frame's slot turns cold again, but its key was already metered for the transaction.
        yield return Case("Accesses after a reverted frame are not re-metered", Prepare.EvmCode.Call(Callee, 50_000).Call(Callee, 50_000), 20 + 32);
        // The reverted write's key and value bytes stay counted; the committed write of the original value adds none.
        yield return Case("Write in a reverted frame stays metered",
            Prepare.EvmCode.DelegateCall(RevertingWriter, 50_000).PushData(5).PushData(1).Op(Instruction.SSTORE), 20 + 32 + 32);
        yield return Case("Restoring write in a reverted frame meters nothing",
            SStore(1, 7).DelegateCall(RevertingRestorer, 50_000), 32 + 32 + 20);
        // Bytes are metered only once the operation's execution gas is paid: a frame too short for the charge counts none.
        yield return Case("SSTORE out of gas on the write charge meters no value bytes", Prepare.EvmCode.Call(StarvedWriter, 8_000), 20 + 32);
        yield return Case("EXTCODECOPY out of gas on its charge meters no address bytes", Prepare.EvmCode.Call(StarvedCopier, 50_000), 20);
        yield return Case("SELFDESTRUCT sweeping to another account", Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.SELFDESTRUCT), 20 + 64);
        yield return Case("SELFDESTRUCT to itself", Prepare.EvmCode.PushData(Executing).Op(Instruction.SELFDESTRUCT), 20);
        // The new address, the creator's nonce and the new contract's nonce.
        yield return Case("CREATE", Prepare.EvmCode.Create([], 0), 20 + 8 + 8);
        yield return Case("CREATE with endowment", Prepare.EvmCode.Create([], 1), 20 + 8 + 8 + 64);
        yield return Case("CREATE deploying code", Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf([1, 2, 3, 4, 5]).Done, 0), 20 + 8 + 8 + 5);
        // SETCODEFROM meters the source on first touch and the adopted code as the executing account's code change.
        yield return Case("SETCODEFROM adopting code", Prepare.EvmCode.SETCODEFROM(CodeSource), 20 + 7);
        yield return Case("SETCODEFROM of an already touched source", Prepare.EvmCode.PushData(CodeSource).Op(Instruction.BALANCE).SETCODEFROM(CodeSource), 20 + 7);
        yield return Case("Repeated SETCODEFROM meters the source and code once", Prepare.EvmCode.SETCODEFROM(CodeSource).SETCODEFROM(CodeSource), 20 + 7);
        yield return Case("SETCODEFROM of its own code writes no code bytes", Prepare.EvmCode.SETCODEFROM(Executing), 20);
        yield return Case("SETCODEFROM of a source without code", Prepare.EvmCode.SETCODEFROM(ColdAccount), 20);
        // An adopted creation skips the code deposit, so the adopted code is metered only by SETCODEFROM.
        yield return Case("CREATE adopting code", Prepare.EvmCode.Create(Prepare.EvmCode.SETCODEFROM(CodeSource).STOP().Done, 0), 20 + 8 + 8 + 20 + 7);

        static TestCaseData Case(string name, Prepare code, int bytes) => new TestCaseData(code.STOP().Done, (ulong)bytes).SetName(name);
    }

    [TestCaseSource(nameof(MeteredBytesCases))]
    public void Floor_is_extended_by_metered_block_access_list_bytes(byte[] code, ulong expectedBytes)
    {
        (ulong gasSpent, ulong staticFloor, _, _) = Run(code, GasLimit);
        Assert.That(gasSpent, Is.EqualTo(staticFloor + expectedBytes * Eip8131Constants.FloorGasPerByte));
    }

    [TestCaseSource(nameof(MeteredBytesCases))]
    public void Floor_is_not_extended_when_eip8279_is_disabled(byte[] code, ulong meteredBytesWhenEnabled)
    {
        (Block block, Transaction tx) = PrepareFloorBindingTx(code, GasLimit);
        (ulong gasSpent, _, _, EthereumVirtualMachine vm) = Execute(block, tx, Spec8131Only);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gasSpent, Is.EqualTo(IntrinsicGasCalculator.Calculate(tx, Spec8131Only).FloorGas));
            Assert.That(vm.TxExecutionContext.BalDataMeter, Is.Null);
        }
    }

    [TestCase(false, TestName = "Meter out of gas aborts before the block access list entry")]
    [TestCase(true, TestName = "Meter within the gas limit records the block access list entry")]
    public void Meter_out_of_gas_leaves_no_block_access_list_entry(bool affordable)
    {
        byte[] code = Prepare.EvmCode.PushData(ColdAccount).Op(Instruction.BALANCE).STOP().Done;
        ulong staticFloor = IntrinsicGasCalculator.Calculate(Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(Executing)
            .WithValue(1)
            .WithData(new byte[FloorBindingCalldataBytes])
            .TestObject, Spec8279).FloorGas;
        ulong gasLimit = staticFloor + Eip8279Constants.AddressBytes * Eip8131Constants.FloorGasPerByte - (affordable ? 0UL : 1UL);

        (ulong gasSpent, _, CallOutputTracer tracer, BlockAccessListAtIndex bal) = Run(code, gasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(affordable ? StatusCode.Success : StatusCode.Failure));
            Assert.That(gasSpent, Is.EqualTo(gasLimit));
            Assert.That(bal.GetAccountChanges(ColdAccount), affordable ? Is.Not.Null : Is.Null);
        }
    }

    [TestCase(Eip8279Constants.AddressBytes + 7, true, TestName = "SETCODEFROM within the floor limit adopts the code")]
    [TestCase(Eip8279Constants.AddressBytes + 6, false, TestName = "SETCODEFROM code meter out of gas aborts before the code write")]
    [TestCase(Eip8279Constants.AddressBytes - 1, false, TestName = "SETCODEFROM source meter out of gas aborts before the source access")]
    public void Setcodefrom_meter_out_of_gas_leaves_no_code_change(ulong affordableBytes, bool affordable)
    {
        byte[] code = Prepare.EvmCode.SETCODEFROM(CodeSource).STOP().Done;
        (Block block, Transaction tx) = PrepareFloorBindingTx(code, GasLimit);
        tx.GasLimit = IntrinsicGasCalculator.Calculate(tx, Spec8279).FloorGas + affordableBytes * Eip8131Constants.FloorGasPerByte;

        (ulong gasSpent, CallOutputTracer tracer, BlockAccessListAtIndex bal, _) = Execute(block, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(affordable ? StatusCode.Success : StatusCode.Failure));
            Assert.That(gasSpent, Is.EqualTo((ulong)tx.GasLimit));
            Assert.That(bal.GetAccountChanges(Executing)?.CodeChange, affordable ? Is.Not.Null : Is.Null);
            Assert.That(bal.GetAccountChanges(CodeSource), affordableBytes >= Eip8279Constants.AddressBytes ? Is.Not.Null : Is.Null);
        }
    }

    [Test]
    public void Contract_creation_transaction_meters_adopted_code()
    {
        byte[] initCode = Bytes.Concat(Prepare.EvmCode.SETCODEFROM(CodeSource).STOP().Done, new byte[FloorBindingCalldataBytes]);
        TestState.CreateAccount(CodeSource, 0);
        TestState.InsertCode(CodeSource, AdoptedCode, Spec8279);
        (Block block, Transaction tx) = PrepareInitTx(Activation, GasLimit, initCode);

        (ulong gasSpent, _, _, _) = Execute(block, tx);

        Assert.That(gasSpent, Is.EqualTo(IntrinsicGasCalculator.Calculate(tx, Spec8279).FloorGas
            + (Eip8279Constants.AddressBytes + (ulong)AdoptedCode.Length) * Eip8131Constants.FloorGasPerByte));
    }

    [Test]
    public void Call_meters_the_delegation_target_once([Values] bool touchedTarget)
    {
        Address authority = new("0x0000000000000000000000000000000000000042");
        TestState.CreateAccount(authority, 0);
        CodeInfoRepository.SetDelegation(ColdAccount, authority, Spec8279);
        Prepare code = Prepare.EvmCode;
        if (touchedTarget) code.PushData(ColdAccount).Op(Instruction.BALANCE).Op(Instruction.POP);
        code.Call(authority, 50_000).STOP();

        (ulong gasSpent, ulong staticFloor, _, _) = Run(code.Done, GasLimit);

        Assert.That(gasSpent, Is.EqualTo(staticFloor + 40 * Eip8131Constants.FloorGasPerByte));
    }

    [Test]
    public void Create_collision_meters_only_the_creator_nonce()
    {
        Address created = ContractAddress.From(Executing, UInt256.Zero);
        TestState.CreateAccount(created, 0, nonce: 1);
        byte[] code = Prepare.EvmCode.Create([], 0).STOP().Done;

        (Block block, Transaction tx) = PrepareFloorBindingTx(code, GasLimit);
        (EthereumVirtualMachine vm, TransactionProcessor<EthereumGasPolicy> processor, _) = CreateProcessor();
        CallOutputTracer tracer = new();

        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, Spec8279), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ToString());
            Assert.That(vm.TxExecutionContext.BalDataMeter!.BalDataBytes, Is.EqualTo(Eip8279Constants.AddressBytes + Eip8279Constants.NonceBytes));
            Assert.That(TestState.GetNonce(created), Is.EqualTo(1UL));
        }
    }

    [Test]
    public void Code_deposit_meter_out_of_gas_fails_opcode_creation()
    {
        byte[] deployed = [1, 2, 3, 4, 5];
        byte[] code = Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(deployed).Done, 0)
            .PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
        (Block block, Transaction tx) = PrepareFloorBindingTx(code, GasLimit);
        ulong staticFloor = IntrinsicGasCalculator.Calculate(tx, Spec8279).FloorGas;
        tx.GasLimit = staticFloor + (Eip8279Constants.AddressBytes + 2 * Eip8279Constants.NonceBytes) * Eip8131Constants.FloorGasPerByte;

        (_, CallOutputTracer tracer, _, _) = Execute(block, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
            Assert.That(TestState.GetCodeSpan(ContractAddress.From(Executing, UInt256.Zero)).IsEmpty, Is.True);
        }
    }

    [Test]
    public void Contract_creation_transaction_meters_deployed_code()
    {
        byte[] deployed = [1, 2, 3, 4, 5, 6, 7];
        byte[] initCode = Bytes.Concat(Prepare.EvmCode.ForInitOf(deployed).Done, new byte[FloorBindingCalldataBytes]);
        (Block block, Transaction tx) = PrepareInitTx(Activation, GasLimit, initCode);

        (ulong gasSpent, _, _, _) = Execute(block, tx);

        Assert.That(gasSpent, Is.EqualTo(IntrinsicGasCalculator.Calculate(tx, Spec8279).FloorGas + (ulong)deployed.Length * Eip8131Constants.FloorGasPerByte));
    }

    [TestCase(0, true, 21_000UL, TestName = "Bare ETH transfer static floor")]
    [TestCase(1, true, 21_000UL + 64 * (108 + 51), TestName = "One authorization static floor")]
    [TestCase(2, true, 21_000UL + 2 * 64 * (108 + 51), TestName = "Two authorizations static floor")]
    [TestCase(2, false, 21_000UL + 2 * 64 * 108, TestName = "Two authorizations static floor without EIP-8279")]
    public void Static_floor_adds_authorization_block_access_list_bytes(int authorizations, bool eip8279, ulong expectedFloor)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether);
        for (int i = 0; i < authorizations; i++)
        {
            builder.WithType(TxType.SetCode).WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressC, 0, new Signature(new byte[64], 0)));
        }

        Assert.That(IntrinsicGasCalculator.Calculate(builder.TestObject, eip8279 ? Spec8279 : Spec8131Only).FloorGas, Is.EqualTo(expectedFloor));
    }

    [Test]
    public void System_calls_are_not_metered()
    {
        TestState.CreateAccount(Executing, 0);
        TestState.InsertCode(Executing, SStore(1, 1).STOP().Done, Spec8279);
        TestState.Commit(Spec8279);
        Transaction tx = new SystemCall { GasLimit = Eip8037Constants.SystemCallGasLimit, To = Executing, SenderAddress = Address.SystemUser };

        (EthereumVirtualMachine vm, TransactionProcessor<EthereumGasPolicy> processor, _) = CreateProcessor();
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(Build.A.BlockHeader.TestObject, Spec8279), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ToString());
            Assert.That(vm.TxExecutionContext.BalDataMeter, Is.Null);
        }
    }

    private (ulong GasSpent, ulong StaticFloor, CallOutputTracer Tracer, BlockAccessListAtIndex Bal) Run(byte[] code, ulong gasLimit)
    {
        (Block block, Transaction tx) = PrepareFloorBindingTx(code, gasLimit);
        (ulong gasSpent, CallOutputTracer tracer, BlockAccessListAtIndex bal, _) = Execute(block, tx);
        return (gasSpent, IntrinsicGasCalculator.Calculate(tx, Spec8279).FloorGas, tracer, bal);
    }

    private (Block Block, Transaction Tx) PrepareFloorBindingTx(byte[] code, ulong gasLimit)
    {
        TestState.CreateAccount(ColdAccount, 1.Ether);
        TestState.CreateAccount(Callee, 0);
        // Callee reads a cold slot, then reverts.
        TestState.InsertCode(Callee, Prepare.EvmCode.PushData(1).Op(Instruction.SLOAD).Revert(0, 0).Done, Spec8279);
        TestState.CreateAccount(RevertingRestorer, 0);
        TestState.InsertCode(RevertingRestorer, SStore(1, 5).Revert(0, 0).Done, Spec8279);
        TestState.CreateAccount(RevertingWriter, 0);
        TestState.InsertCode(RevertingWriter, SStore(1, 7).Revert(0, 0).Done, Spec8279);
        TestState.CreateAccount(StarvedWriter, 0);
        TestState.InsertCode(StarvedWriter, SStore(2, 1).Done, Spec8279);
        TestState.CreateAccount(StarvedCopier, 0);
        TestState.InsertCode(StarvedCopier, Prepare.EvmCode.PushData(1_000_000).PushData(0).PushData(0).PushData(ColdAccount).Op(Instruction.EXTCODECOPY).Done, Spec8279);
        TestState.CreateAccount(CodeSource, 0);
        TestState.InsertCode(CodeSource, AdoptedCode, Spec8279);
        TestState.CreateAccount(Executing, 1.Ether);
        TestState.Set(new StorageCell(Executing, 1), (UInt256)5);
        return PrepareTx(Activation, gasLimit, code, new byte[FloorBindingCalldataBytes], 1);
    }

    private (ulong GasSpent, CallOutputTracer Tracer, BlockAccessListAtIndex Bal, EthereumVirtualMachine Vm) Execute(Block block, Transaction tx, IReleaseSpec? spec = null)
    {
        (EthereumVirtualMachine vm, TransactionProcessor<EthereumGasPolicy> processor, TracedAccessWorldState tracedState) = CreateProcessor();
        CallOutputTracer tracer = new();
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, spec ?? Spec8279), tracer);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());
        return (tracer.GasSpent, tracer, tracedState.GetGeneratingBlockAccessList()!, vm);
    }

    private (EthereumVirtualMachine Vm, TransactionProcessor<EthereumGasPolicy> Processor, TracedAccessWorldState TracedState) CreateProcessor()
    {
        TracedAccessWorldState tracedState = new(TestState, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance);
        TransactionProcessor<EthereumGasPolicy> processor = new(
            BlobBaseFeeCalculator.Instance, SpecProvider, tracedState, vm, new EthereumCodeInfoRepository(tracedState), LimboLogs.Instance);
        return (vm, processor, tracedState);
    }
}
