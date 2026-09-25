// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Zk gas of failing steps, pinned to the values taiko-geth checks against alethia-reth in
/// <c>core/vm/taiko_zk_gas_runtime_test.go</c>. Only the named opcode is metered, with multiplier 1
/// unless stated, and the frame gas is exact (the transaction carries no calldata).
/// </summary>
public class ZkGasRevmParityTests : VirtualMachineTestsBase
{
    private const string PushMax = "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string PushZeroAddress = "730000000000000000000000000000000000000000";
    private const string InnerHex = "2000000000000000000000000000000000000000";
    private const string TargetHex = "3000000000000000000000000000000000000000";

    private static readonly Address Inner = new("0x" + InnerHex);

    protected override ForkActivation Activation => MainnetSpecProvider.OsakaActivation;

    [TestCase(Instruction.ADD, 5, "600160020100", 8UL, 10UL, TestName = "Static gas out of gas spends the step's gas")]
    [TestCase(Instruction.MSTORE, 7, "60ff60005200", 11UL, 21UL, TestName = "MSTORE memory expansion out of gas charges static gas")]
    [TestCase(Instruction.MSTORE, 22, "60217f80000000000000000000000000000000000000000000000000000000000000005200", 100_000UL, 66UL, TestName = "MSTORE offset overflow charges static gas (taiko block 4796)")]
    [TestCase(Instruction.KECCAK256, 7, "604060002000", 51UL, 294UL, TestName = "KECCAK256 memory expansion out of gas charges static and word gas")]
    [TestCase(Instruction.CALL, 7, "6000600060206000600060016000f100", 123UL, 700UL, TestName = "CALL memory expansion out of gas charges static gas")]
    public void Out_of_gas_step(Instruction opcode, int multiplier, string code, ulong frameGas, ulong expected) =>
        Assert.That(Run(code, frameGas, opcode, (ushort)multiplier), Is.EqualTo(expected));

    [TestCase(Instruction.ADD, "01", 100_000UL, 3UL, TestName = "ADD stack underflow")]
    [TestCase(Instruction.SLOAD, "54", 100_000UL, 100UL, TestName = "SLOAD stack underflow")]
    [TestCase(Instruction.LOG0, "a0", 100_000UL, 375UL, TestName = "LOG0 stack underflow")]
    [TestCase(Instruction.EXP, "0a", 100_000UL, 10UL, TestName = "EXP stack underflow")]
    [TestCase(Instruction.CREATE, "f0", 100_000UL, 0UL, TestName = "CREATE stack underflow")]
    [TestCase(Instruction.SWAP1, "90", 100_000UL, 3UL, TestName = "SWAP1 stack underflow")]
    [TestCase(Instruction.ADD, "01", 2UL, 2UL, TestName = "ADD stack underflow with unpayable static gas")]
    [TestCase(Instruction.DUPN, "e6", 100_000UL, 3UL, TestName = "DUPN not activated")]
    [TestCase(Instruction.SLOTNUM, "4b", 100_000UL, 2UL, TestName = "SLOTNUM not activated")]
    public void Pre_execution_failure(Instruction opcode, string code, ulong frameGas, ulong expected) =>
        Assert.That(Run(code, frameGas, opcode), Is.EqualTo(expected));

    [Test]
    public void Push0_stack_overflow() =>
        Assert.That(Run(string.Concat(Enumerable.Repeat("5f", 1025)), 1_000_000, Instruction.PUSH0), Is.EqualTo(1025UL * 2));

    [TestCase("602063ffffffff6000f000", 20_000UL, 2UL, TestName = "CREATE unaffordable memory charges initcode cost")]
    [TestCase("6000602063ffffffff6000f500", 20_000UL, 2UL, TestName = "CREATE2 unaffordable memory charges initcode cost")]
    [TestCase("61c00060006000f000", 2_000UL, 1_991UL, TestName = "CREATE unaffordable initcode cost spends all")]
    [TestCase("61c00160006000f000", 20_000UL, 0UL, TestName = "CREATE oversized initcode charges nothing")]
    [TestCase("61c00160006000f000", 100_000UL, 0UL, TestName = "CREATE oversized initcode charges nothing with gas for the base cost")]
    [TestCase("600061c00160006000f500", 100_000UL, 0UL, TestName = "CREATE2 oversized initcode charges nothing with gas for the base cost")]
    [TestCase("602060006000f000", 20_000UL, 19_991UL, TestName = "CREATE unaffordable base cost spends all")]
    [TestCase("6020" + PushMax + "6000f000", 100_000UL, 2UL, TestName = "CREATE offset overflow charges initcode cost")]
    [TestCase(PushMax + "60006000f000", 100_000UL, 0UL, TestName = "CREATE size overflow charges nothing")]
    [TestCase("6020650100000000006000f000", 100_000UL, 2UL, TestName = "CREATE memory cost overflow charges initcode cost")]
    [TestCase("602060006000f500", 20_000UL, 5UL, TestName = "CREATE2 salt underflow charges initcode and memory")]
    [TestCase("600060006000f500", 20_000UL, 0UL, TestName = "CREATE2 salt underflow with zero length charges nothing")]
    [TestCase("60006000f500", 20_000UL, 0UL, TestName = "CREATE2 two operands charge nothing")]
    [TestCase("61c00060006000f500", 2_000UL, 1_991UL, TestName = "CREATE2 salt underflow with unaffordable initcode cost spends all")]
    [TestCase("61c00160006000f500", 20_000UL, 0UL, TestName = "CREATE2 salt underflow with oversized initcode charges nothing")]
    [TestCase(PushMax + "60006000f500", 100_000UL, 0UL, TestName = "CREATE2 salt underflow with length overflow charges nothing")]
    [TestCase("6020" + PushMax + "6000f500", 100_000UL, 2UL, TestName = "CREATE2 salt underflow with offset overflow charges initcode cost")]
    [TestCase("602063ffffffff6000f500", 20_000UL, 2UL, TestName = "CREATE2 salt underflow with unaffordable memory charges initcode cost")]
    [TestCase("6020650100000000006000f500", 100_000UL, 2UL, TestName = "CREATE2 salt underflow with memory cost overflow charges initcode cost")]
    public void Create_family(string code, ulong frameGas, ulong expected) =>
        Assert.That(Run(code, frameGas, Instruction.CREATE, Instruction.CREATE2), Is.EqualTo(expected));

    [TestCase(Instruction.LOG0, PushMax + "6000a000", 100_000UL, 375UL, TestName = "LOG0 length overflow charges static gas")]
    [TestCase(Instruction.LOG0, "6020" + PushMax + "a000", 100_000UL, 631UL, TestName = "LOG0 offset overflow charges static and data gas")]
    [TestCase(Instruction.LOG1, "60006020" + PushMax + "a100", 100_000UL, 1_006UL, TestName = "LOG1 offset overflow includes topic gas")]
    [TestCase(Instruction.LOG0, "602065020000000000a000", 100_000UL, 631UL, TestName = "LOG0 memory cost cap charges static and data gas")]
    [TestCase(Instruction.LOG0, "6740000000000000006000a000", 100_000UL, 99_994UL, TestName = "LOG0 unaffordable data cost spends all")]
    [TestCase(Instruction.LOG0, "602063ffffffffa000", 20_000UL, 631UL, TestName = "LOG0 unaffordable memory charges static and data gas")]
    [TestCase(Instruction.LOG1, "60006000a100", 100_000UL, 750UL, TestName = "LOG1 missing topic charges static and topic gas")]
    [TestCase(Instruction.LOG2, "600060006000a200", 100_000UL, 1_125UL, TestName = "LOG2 one topic short charges static and topic gas")]
    [TestCase(Instruction.LOG4, "60006000a400", 100_000UL, 1_875UL, TestName = "LOG4 missing topics charge static and topic gas")]
    [TestCase(Instruction.LOG1, "60206000a100", 100_000UL, 1_009UL, TestName = "LOG1 missing topic with data charges memory too")]
    [TestCase(Instruction.LOG1, "6000a100", 100_000UL, 375UL, TestName = "LOG1 single operand charges static gas")]
    public void Log_family(Instruction opcode, string code, ulong frameGas, ulong expected) =>
        Assert.That(Run(code, frameGas, opcode), Is.EqualTo(expected));

    [TestCase(Instruction.KECCAK256, "6502000000000060002000", 100_000UL, 99_994UL, TestName = "KECCAK256 unpayable word cost spends all")]
    [TestCase(Instruction.KECCAK256, "6020650200000000002000", 100_000UL, 36UL, TestName = "KECCAK256 capped memory charges static and word gas")]
    [TestCase(Instruction.KECCAK256, "602063ffffffff2000", 20_000UL, 36UL, TestName = "KECCAK256 unaffordable memory charges static and word gas")]
    [TestCase(Instruction.KECCAK256, "6020" + PushMax + "2000", 100_000UL, 36UL, TestName = "KECCAK256 offset overflow charges static and word gas")]
    [TestCase(Instruction.KECCAK256, PushMax + "60002000", 100_000UL, 30UL, TestName = "KECCAK256 length overflow charges static gas")]
    [TestCase(Instruction.CALLDATACOPY, "60206000650200000000003700", 100_000UL, 6UL, TestName = "CALLDATACOPY capped memory charges static and copy gas")]
    [TestCase(Instruction.CALLDATACOPY, "60206000" + PushMax + "3700", 100_000UL, 6UL, TestName = "CALLDATACOPY offset overflow charges static and copy gas")]
    [TestCase(Instruction.CODECOPY, "60206000650200000000003900", 100_000UL, 6UL, TestName = "CODECOPY capped memory charges static and copy gas")]
    [TestCase(Instruction.EXTCODECOPY, "6020600065020000000000" + PushZeroAddress + "3c00", 100_000UL, 103UL, TestName = "EXTCODECOPY capped memory charges static and copy gas")]
    [TestCase(Instruction.EXTCODECOPY, "6020600063ffffffff" + PushZeroAddress + "3c00", 20_000UL, 103UL, TestName = "EXTCODECOPY unaffordable memory charges static and copy gas")]
    [TestCase(Instruction.EXTCODECOPY, "60206000" + PushMax + PushZeroAddress + "3c00", 100_000UL, 103UL, TestName = "EXTCODECOPY offset overflow charges static and copy gas")]
    [TestCase(Instruction.EXTCODECOPY, PushMax + "60006000" + PushZeroAddress + "3c00", 100_000UL, 100UL, TestName = "EXTCODECOPY length overflow charges static gas")]
    [TestCase(Instruction.MCOPY, "60206000650200000000005e00", 100_000UL, 6UL, TestName = "MCOPY capped memory charges static and copy gas")]
    [TestCase(Instruction.MCOPY, "6020" + PushMax + "60005e00", 100_000UL, 6UL, TestName = "MCOPY source overflow charges static and copy gas")]
    [TestCase(Instruction.RETURNDATACOPY, "6020600060003e00", 100_000UL, 3UL, TestName = "RETURNDATACOPY out of bounds charges static gas")]
    [TestCase(Instruction.RETURNDATACOPY, "611000600063ffffffff3e00", 20_000UL, 3UL, TestName = "RETURNDATACOPY unaffordable out of bounds copy charges static gas")]
    [TestCase(Instruction.MLOAD, "650200000000005100", 100_000UL, 3UL, TestName = "MLOAD capped memory charges static gas")]
    [TestCase(Instruction.MLOAD, PushMax + "5100", 100_000UL, 3UL, TestName = "MLOAD offset overflow charges static gas")]
    [TestCase(Instruction.MSTORE, "6000650200000000005200", 100_000UL, 3UL, TestName = "MSTORE capped memory charges static gas")]
    [TestCase(Instruction.MSTORE8, "6000650200000000005300", 100_000UL, 3UL, TestName = "MSTORE8 capped memory charges static gas")]
    [TestCase(Instruction.RETURN, "602065020000000000f3", 100_000UL, 0UL, TestName = "RETURN capped memory charges nothing")]
    public void Memory_and_copy_family(Instruction opcode, string code, ulong frameGas, ulong expected) =>
        Assert.That(Run(code, frameGas, opcode), Is.EqualTo(expected));

    [TestCase(Instruction.CALL, "602065020000000000600060006000" + PushZeroAddress + "61fffff100", 100UL, TestName = "CALL capped output memory charges static gas")]
    [TestCase(Instruction.DELEGATECALL, "60206502000000000060006000" + PushZeroAddress + "61fffff400", 100UL, TestName = "DELEGATECALL capped output memory charges static gas")]
    [TestCase(Instruction.CALL, "602065020000000000602060006000" + PushZeroAddress + "61fffff100", 103UL, TestName = "CALL capped output after input expansion charges input memory")]
    [TestCase(Instruction.CALL, "602065020000000000604060006000" + PushZeroAddress + "61fffff100", 106UL, TestName = "CALL capped output after two-word input expansion")]
    [TestCase(Instruction.DELEGATECALL, "60206502000000000060206000" + PushZeroAddress + "61fffff400", 103UL, TestName = "DELEGATECALL capped output after input expansion charges input memory")]
    [TestCase(Instruction.CALL, "600060006020650200000000006000" + PushZeroAddress + "61fffff100", 100UL, TestName = "CALL capped input memory charges static gas")]
    [TestCase(Instruction.CALL, "6020" + PushMax + "602060006000" + PushZeroAddress + "61fffff100", 103UL, TestName = "CALL output offset overflow after input expansion")]
    [TestCase(Instruction.CALL, "600060006020" + PushMax + "6000" + PushZeroAddress + "61fffff100", 100UL, TestName = "CALL input offset overflow charges static gas")]
    public void Call_family(Instruction opcode, string code, ulong expected) =>
        Assert.That(Run(code, 100_000, opcode), Is.EqualTo(expected));

    [TestCase(Instruction.CREATE, "602063ffffffff6000f000", 0UL, TestName = "Static CREATE shortfall charges nothing")]
    [TestCase(Instruction.CREATE2, "6000602063ffffffff6000f500", 0UL, TestName = "Static CREATE2 shortfall charges nothing")]
    [TestCase(Instruction.CREATE2, "602060006000f500", 0UL, TestName = "Static CREATE2 salt underflow charges nothing")]
    [TestCase(Instruction.CREATE, "600060006000f0", 0UL, TestName = "Static CREATE write protection charges nothing")]
    [TestCase(Instruction.CREATE2, "6000600060006000f5", 0UL, TestName = "Static CREATE2 write protection charges nothing")]
    [TestCase(Instruction.LOG0, "61100063ffffffffa000", 375UL, TestName = "Static LOG0 unaffordable memory charges static gas")]
    [TestCase(Instruction.LOG2, "6000600061100063ffffffffa200", 375UL, TestName = "Static LOG2 unaffordable memory charges static gas")]
    [TestCase(Instruction.LOG0, PushMax + "6000a000", 375UL, TestName = "Static LOG0 length overflow charges static gas")]
    [TestCase(Instruction.LOG0, "602065020000000000a000", 375UL, TestName = "Static LOG0 memory cost cap charges static gas")]
    [TestCase(Instruction.LOG0, "60206000a000", 375UL, TestName = "Static LOG0 write protection charges static gas")]
    [TestCase(Instruction.LOG1, "60006000a100", 375UL, TestName = "Static LOG1 topic underflow charges static gas")]
    [TestCase(Instruction.TSTORE, "600060005d00", 100UL, TestName = "Static TSTORE charges static gas")]
    [TestCase(Instruction.SSTORE, "600160005500", 0UL, TestName = "Static SSTORE charges nothing")]
    [TestCase(Instruction.CALL, "6000600060006000600173" + TargetHex + "61fffff100", 100UL, TestName = "Static CALL with value charges static gas")]
    [TestCase(Instruction.SELFDESTRUCT, "73" + TargetHex + "ff00", 5_000UL, TestName = "Static SELFDESTRUCT charges static gas")]
    [TestCase(Instruction.CALL, "6020" + PushMax + "602060006001" + PushZeroAddress + "61fffff100", 100UL, TestName = "Static CALL with value halts before memory charges")]
    [TestCase(Instruction.CALL, "6020" + PushMax + "602060006000" + PushZeroAddress + "61fffff100", 103UL, TestName = "Static CALL without value charges input memory")]
    [TestCase(Instruction.CALLCODE, "6020" + PushMax + "602060006001" + PushZeroAddress + "61fffff200", 103UL, TestName = "Static CALLCODE with value charges input memory")]
    public void Static_frame(Instruction opcode, string innerCode, ulong expected) =>
        Assert.That(Run(CallInner(Instruction.STATICCALL, 0xffff), 200_000, [opcode], innerCode: innerCode), Is.EqualTo(expected));

    [Test]
    public void Static_log_write_protection_uses_multiplier() =>
        Assert.That(Run(CallInner(Instruction.STATICCALL, 0xffff), 200_000, [Instruction.LOG1], multiplier: 7, innerCode: "600060006000a1"),
            Is.EqualTo(375UL * 7));

    [Test]
    public void Sstore_reentrancy_sentry_charges_nothing() =>
        Assert.That(Run(CallInner(Instruction.CALL, 2306), 200_000, [Instruction.SSTORE], innerCode: "600160005500"), Is.EqualTo(0UL));

    [TestCase(Instruction.EXP, "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff60020a00", (ushort)100, 94UL, TestName = "EXP unaffordable exponent bytes spend all")]
    [TestCase(Instruction.SLOAD, "60005400", (ushort)150, 147UL, TestName = "Cold SLOAD unaffordable spends all")]
    [TestCase(Instruction.BALANCE, "7300000000000000000000000000000000deadbeef3100", (ushort)150, 147UL, TestName = "Cold BALANCE unaffordable spends all")]
    [TestCase(Instruction.EXTCODESIZE, "7300000000000000000000000000000000deadbeef3b00", (ushort)150, 147UL, TestName = "Cold EXTCODESIZE unaffordable spends all")]
    public void Dynamic_shortfall_spends_all(Instruction opcode, string innerCode, ushort childGas, ulong expected) =>
        Assert.That(Run(CallInner(Instruction.CALL, childGas), 200_000, [opcode], innerCode: innerCode), Is.EqualTo(expected));

    [Test]
    public void Call_out_of_funds_counts_as_spawned() =>
        Assert.That(Run("6000600060006000" + PushMax + "73" + TargetHex + "612710f100", 100_000, Instruction.CALL),
            Is.EqualTo(ZkGasSchedule.SpawnEstimateCall));

    [TestCase(Instruction.RETURN, "60206000f3", 3UL, TestName = "Successful RETURN charges its memory expansion")]
    [TestCase(Instruction.REVERT, "60206000fd", 3UL, TestName = "REVERT charges its memory expansion")]
    [TestCase(Instruction.MSTORE, "600160005200", 6UL, TestName = "Successful MSTORE charges static and memory gas")]
    [TestCase(Instruction.KECCAK256, "602060002000", 39UL, TestName = "Successful KECCAK256 charges static, word and memory gas")]
    public void Successful_step_keeps_measured_gas(Instruction opcode, string code, ulong expected) =>
        Assert.That(Run(code, 100_000, opcode), Is.EqualTo(expected));

    private ulong Run(string code, ulong frameGas, params Instruction[] metered) => Run(code, frameGas, metered, 1);

    private ulong Run(string code, ulong frameGas, Instruction metered, ushort multiplier) => Run(code, frameGas, [metered], multiplier);

    private ulong Run(string code, ulong frameGas, Instruction[] metered, ushort multiplier = 1, string? innerCode = null)
    {
        ushort[] multipliers = new ushort[256];
        foreach (Instruction opcode in metered)
        {
            multipliers[(byte)opcode] = multiplier;
        }

        if (innerCode is not null)
        {
            TestState.CreateAccount(Inner, UInt256.Zero);
            TestState.InsertCode(Inner, Bytes.FromHexString(innerCode), SpecProvider.GenesisSpec);
        }

        ZkGasMeter meter = new(blockZkGasLimit: 1UL << 40, txIntrinsicZkGas: 0, opcodeMultipliers: multipliers);
        (Block block, Transaction tx) = PrepareTx(Activation, 21_000 + frameGas, Bytes.FromHexString(code));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), new ZkGasTxTracer(meter));
        return meter.TxZkGasUsed;
    }

    private static string CallInner(Instruction opcode, ushort gas) =>
        "6000600060006000" + (opcode is Instruction.CALL or Instruction.CALLCODE ? "6000" : "")
        + "73" + InnerHex + "61" + gas.ToString("x4") + ((byte)opcode).ToString("x2") + "00";
}
