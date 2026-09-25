// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Gas left after a failed opcode as REVM's inspector sees it.
/// </summary>
/// <remarks>
/// Unzen zk gas follows REVM (alethia-reth): the instruction table's static gas is deducted before the
/// instruction body runs, the body then charges in its own order, and a halt on a stack, operand or
/// memory check keeps whatever gas was not charged yet. Only a failed <c>gas!</c> charge spends all
/// remaining gas. Nethermind's VM validates in a different order and reports zero gas for every
/// out-of-gas failure, so the raw per-step delta of a failed step does not match. The reconstruction
/// below follows taiko-geth's <c>core/vm/taiko_zk_gas_runtime.go</c>, which pins the same values
/// against alethia-reth.
/// </remarks>
internal static class RevmFailedStepGas
{
    private const ulong MaxInitCodeSize = 2 * CodeSizeConstants.MaxCodeSizeEip170;
    private const int WordSize = 32;

    /// <summary>
    /// Returns the gas REVM has left after <paramref name="opcode"/> failed with <paramref name="error"/>.
    /// </summary>
    /// <param name="stack">Stack as it was when the step started (top of stack is index 0 for peeks).</param>
    /// <param name="memorySize">Active memory size in bytes when the step started.</param>
    /// <param name="reportedGasAfter">Gas Nethermind reported after the step, used where both VMs agree.</param>
    public static ulong GasAfter(
        Instruction opcode,
        EvmExceptionType error,
        ulong gasBefore,
        ulong reportedGasAfter,
        in TraceStack stack,
        ulong memorySize,
        int returnDataLength)
    {
        switch (error)
        {
            // Nethermind checks the static context before any stack, operand or memory check of the same
            // opcode, and REVM halts right after the static gas there too, so frames need no static tracking.
            case EvmExceptionType.StaticCallViolation:
            case EvmExceptionType.StackOverflow:
                return PreExecution(opcode, gasBefore);
            case EvmExceptionType.StackUnderflow:
                // CREATE2 pops its salt, and LOG1-LOG4 their topics, only after the in-body charges.
                if (opcode == Instruction.CREATE2 && stack.Count == 3)
                    return CreateBody(stack, memorySize, gasBefore, out _);
                if (opcode is >= Instruction.LOG1 and <= Instruction.LOG4 && stack.Count >= 2)
                    return Log(opcode, stack, memorySize, gasBefore);
                return PreExecution(opcode, gasBefore);
            case EvmExceptionType.BadInstruction:
                // REVM ships these behind Amsterdam: it deducts their static gas, then halts as not activated.
                return opcode is Instruction.SLOTNUM or Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE
                    ? PreExecution(opcode, gasBefore)
                    : reportedGasAfter;
        }

        switch (opcode)
        {
            case Instruction.CREATE or Instruction.CREATE2 when stack.Count >= 3:
                // The trailing base (+ hashing) charge is the only one left once the body got that far,
                // and it spends everything when it cannot be paid.
                ulong gasAfterBody = CreateBody(stack, memorySize, gasBefore, out bool reachedTail);
                return reachedTail ? 0 : gasAfterBody;
            case >= Instruction.LOG0 and <= Instruction.LOG4 when stack.Count >= 2:
                return Log(opcode, stack, memorySize, gasBefore);
            case Instruction.KECCAK256 when stack.Count >= 2:
            case Instruction.CALLDATACOPY or Instruction.CODECOPY or Instruction.RETURNDATACOPY or Instruction.MCOPY when stack.Count >= 3:
            case Instruction.EXTCODECOPY when stack.Count >= 4:
                return Copy(opcode, stack, memorySize, returnDataLength, gasBefore);
            case Instruction.CALL or Instruction.CALLCODE when stack.Count >= 7:
            case Instruction.DELEGATECALL or Instruction.STATICCALL when stack.Count >= 6:
                return Call(opcode, stack, memorySize, gasBefore);
            case Instruction.MLOAD or Instruction.MSTORE or Instruction.MSTORE8 or Instruction.RETURN or Instruction.REVERT:
                // Nothing is charged in the body before the memory expansion, which halts keeping gas.
                return PreExecution(opcode, gasBefore);
            case Instruction.SSTORE when error == EvmExceptionType.OutOfGas && gasBefore <= GasCostOf.CallStipend:
                // EIP-2200 reentrancy sentry halts before any in-body charge.
                return gasBefore;
            default:
                return reportedGasAfter;
        }
    }

    /// <summary>Gas left once REVM deducted the instruction table's static gas, or zero if it could not.</summary>
    private static ulong PreExecution(Instruction opcode, ulong gasBefore)
    {
        ulong staticGas = StaticGas(opcode);
        return gasBefore < staticGas ? 0 : gasBefore - staticGas;
    }

    /// <summary>
    /// CREATE/CREATE2 body up to, but excluding, the trailing base charge: size validation halts before any
    /// charge, the EIP-3860 word cost spends everything when unaffordable, and the memory expansion halts
    /// keeping gas.
    /// </summary>
    private static ulong CreateBody(in TraceStack stack, ulong memorySize, ulong gasBefore, out bool reachedTail)
    {
        reachedTail = false;

        UInt256 size = stack.PeekUInt256(2);
        if (!size.IsUint64) return gasBefore;
        if (size.u0 == 0)
        {
            reachedTail = true;
            return gasBefore;
        }

        if (size.u0 > MaxInitCodeSize) return gasBefore;

        ulong initCodeCost = Words(size.u0) * GasCostOf.InitCodeWord;
        if (gasBefore < initCodeCost) return 0;

        ulong gas = gasBefore - initCodeCost;
        if (!TryChargeMemory(ref gas, ref memorySize, stack.PeekUInt256(1), size)) return gas;

        reachedTail = true;
        return gas;
    }

    /// <summary>
    /// LOG: static gas, a length beyond 64 bits halts before the topic+data charge, that charge spends
    /// everything when unaffordable, and the offset or memory expansion halt keeps gas. Topics are popped last.
    /// </summary>
    private static ulong Log(Instruction opcode, in TraceStack stack, ulong memorySize, ulong gasBefore)
    {
        if (gasBefore < GasCostOf.Log) return 0;

        ulong gas = gasBefore - GasCostOf.Log;
        UInt256 length = stack.PeekUInt256(1);
        if (!length.IsUint64) return gas;

        UInt128 bodyCost = (UInt128)(ulong)(opcode - Instruction.LOG0) * GasCostOf.LogTopic + (UInt128)length.u0 * GasCostOf.LogData;
        if (bodyCost > gas) return 0;

        gas -= (ulong)bodyCost;
        TryChargeMemory(ref gas, ref memorySize, stack.PeekUInt256(0), length);
        return gas;
    }

    /// <summary>
    /// KECCAK256 and the copy family: static gas, a length beyond 64 bits (or, for RETURNDATACOPY, a source
    /// outside the return buffer) halts keeping gas, the per-word cost spends everything when unaffordable,
    /// and the offset or memory expansion halt keeps gas. EXTCODECOPY's trailing cold-access surcharge
    /// spends everything.
    /// </summary>
    private static ulong Copy(Instruction opcode, in TraceStack stack, ulong memorySize, int returnDataLength, ulong gasBefore)
    {
        ulong staticGas = StaticGas(opcode);
        if (gasBefore < staticGas) return 0;

        ulong gas = gasBefore - staticGas;
        (int lengthIndex, int destinationIndex, ulong wordGas) = opcode switch
        {
            Instruction.KECCAK256 => (1, 0, GasCostOf.Sha3Word),
            Instruction.EXTCODECOPY => (3, 1, GasCostOf.VeryLow),
            _ => (2, 0, GasCostOf.VeryLow),
        };

        UInt256 length = stack.PeekUInt256(lengthIndex);
        if (!length.IsUint64) return gas;

        if (opcode == Instruction.RETURNDATACOPY)
        {
            UInt256 sourceOffset = stack.PeekUInt256(1);
            if (!sourceOffset.IsUint64 || (UInt128)sourceOffset.u0 + length.u0 > (ulong)returnDataLength) return gas;
        }

        UInt128 wordCost = (UInt128)Words(length.u0) * wordGas;
        if (wordCost > gas) return 0;

        gas -= (ulong)wordCost;
        UInt256 offset = stack.PeekUInt256(destinationIndex);
        if (opcode == Instruction.MCOPY && !length.IsZero)
        {
            // MCOPY expands memory to cover both the source and the destination range.
            UInt256 source = stack.PeekUInt256(1);
            if (!offset.IsUint64 || !source.IsUint64) return gas;
            if (source.u0 > offset.u0) offset = source;
        }

        if (!TryChargeMemory(ref gas, ref memorySize, offset, length)) return gas;
        return opcode == Instruction.EXTCODECOPY ? 0 : gas;
    }

    /// <summary>
    /// CALL family: static gas, then the input range is resized and charged before the output range, each
    /// halt keeping gas. The charges that follow (account access, value transfer, new account) spend everything.
    /// </summary>
    private static ulong Call(Instruction opcode, in TraceStack stack, ulong memorySize, ulong gasBefore)
    {
        ulong staticGas = StaticGas(opcode);
        if (gasBefore < staticGas) return 0;

        ulong gas = gasBefore - staticGas;

        int inOffset = opcode is Instruction.CALL or Instruction.CALLCODE ? 3 : 2;
        if (!TryChargeMemory(ref gas, ref memorySize, stack.PeekUInt256(inOffset), stack.PeekUInt256(inOffset + 1))) return gas;
        if (!TryChargeMemory(ref gas, ref memorySize, stack.PeekUInt256(inOffset + 2), stack.PeekUInt256(inOffset + 3))) return gas;
        return 0;
    }

    /// <summary>
    /// Charges the expansion for <c>[offset, offset + length)</c>. Returns <c>false</c> when REVM halts on
    /// the range: an operand beyond 64 bits or an expansion that cannot be paid.
    /// </summary>
    private static bool TryChargeMemory(ref ulong gas, ref ulong memorySize, in UInt256 offset, in UInt256 length)
    {
        if (length.IsZero) return true;
        if (!length.IsUint64 || !offset.IsUint64) return false;

        UInt128 end = (UInt128)offset.u0 + length.u0;
        if (end <= memorySize) return true;

        UInt128 newWords = (end + (WordSize - 1)) / WordSize;
        UInt128 currentWords = memorySize / WordSize;
        UInt128 cost = MemoryCost(newWords) - MemoryCost(currentWords);
        if (cost > gas) return false;

        gas -= (ulong)cost;
        memorySize = (ulong)(newWords * WordSize);
        return true;
    }

    private static UInt128 MemoryCost(UInt128 words) => words * GasCostOf.Memory + words * words / 512;

    private static ulong Words(ulong length) => length / WordSize + (length % WordSize == 0 ? 0UL : 1UL);

    /// <summary>
    /// Static gas REVM's instruction table deducts before the instruction body (revm-interpreter, Osaka
    /// with the Berlin repricing). Several base costs Nethermind charges as dynamic gas (SLOAD, EXP, LOG)
    /// are static here, CREATE/CREATE2 charge their 32000 inside the body, and the Amsterdam opcodes REVM
    /// already ships keep their table cost.
    /// </summary>
    internal static ulong StaticGas(Instruction opcode) => opcode switch
    {
        Instruction.JUMPDEST => 1,
        Instruction.ADDRESS or Instruction.ORIGIN or Instruction.CALLER or Instruction.CALLVALUE
            or Instruction.CALLDATASIZE or Instruction.CODESIZE or Instruction.GASPRICE or Instruction.RETURNDATASIZE
            or Instruction.COINBASE or Instruction.TIMESTAMP or Instruction.NUMBER or Instruction.PREVRANDAO
            or Instruction.GASLIMIT or Instruction.CHAINID or Instruction.BASEFEE or Instruction.BLOBBASEFEE
            or Instruction.SLOTNUM or Instruction.POP or Instruction.PC or Instruction.MSIZE or Instruction.GAS
            or Instruction.PUSH0 => 2,
        Instruction.ADD or Instruction.SUB or Instruction.LT or Instruction.GT or Instruction.SLT or Instruction.SGT
            or Instruction.EQ or Instruction.ISZERO or Instruction.AND or Instruction.OR or Instruction.XOR
            or Instruction.NOT or Instruction.BYTE or Instruction.SHL or Instruction.SHR or Instruction.SAR
            or Instruction.CALLDATALOAD or Instruction.CALLDATACOPY or Instruction.CODECOPY or Instruction.RETURNDATACOPY
            or Instruction.BLOBHASH or Instruction.MLOAD or Instruction.MSTORE or Instruction.MSTORE8 or Instruction.MCOPY
            or Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE => 3,
        >= Instruction.PUSH1 and <= Instruction.SWAP16 => 3,
        Instruction.MUL or Instruction.DIV or Instruction.SDIV or Instruction.MOD or Instruction.SMOD
            or Instruction.SIGNEXTEND or Instruction.CLZ or Instruction.SELFBALANCE => 5,
        Instruction.ADDMOD or Instruction.MULMOD or Instruction.JUMP => 8,
        Instruction.EXP or Instruction.JUMPI => 10,
        Instruction.BLOCKHASH => 20,
        Instruction.KECCAK256 => 30,
        Instruction.BALANCE or Instruction.EXTCODESIZE or Instruction.EXTCODECOPY or Instruction.EXTCODEHASH
            or Instruction.SLOAD or Instruction.TLOAD or Instruction.TSTORE
            or Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL => 100,
        >= Instruction.LOG0 and <= Instruction.LOG4 => 375,
        Instruction.SELFDESTRUCT => 5000,
        _ => 0,
    };
}
