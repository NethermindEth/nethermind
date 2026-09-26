// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Blockchain.Tracing;

/// <summary>The error text the JSON-RPC API reports when a transaction's outermost frame fails.</summary>
/// <remarks>
/// A failure the EVM describes the same way is reported as is. An invalid opcode, a stack underflow and a stack
/// overflow name the failing operation and the stack depth, which a rerun with an operation tracer recovers, so a
/// transaction that succeeds pays nothing for them. Every other failure has no such text and keeps its own.
/// </remarks>
public static class ExecutionFailureText
{
    private const int StackLimit = 1024;
    private const int NotOnTheStackTable = -1;

    /// <summary>Operations indexed by opcode: the mnemonic, and the stack items taken and left by the ones executed.</summary>
    private static readonly Operation?[] Operations = BuildOperations(
    [
        new(0x00, "STOP", 0, 0),
        new(0x01, "ADD", 2, 1),
        new(0x02, "MUL", 2, 1),
        new(0x03, "SUB", 2, 1),
        new(0x04, "DIV", 2, 1),
        new(0x05, "SDIV", 2, 1),
        new(0x06, "MOD", 2, 1),
        new(0x07, "SMOD", 2, 1),
        new(0x08, "ADDMOD", 3, 1),
        new(0x09, "MULMOD", 3, 1),
        new(0x0a, "EXP", 2, 1),
        new(0x0b, "SIGNEXTEND", 2, 1),
        new(0x10, "LT", 2, 1),
        new(0x11, "GT", 2, 1),
        new(0x12, "SLT", 2, 1),
        new(0x13, "SGT", 2, 1),
        new(0x14, "EQ", 2, 1),
        new(0x15, "ISZERO", 1, 1),
        new(0x16, "AND", 2, 1),
        new(0x17, "OR", 2, 1),
        new(0x18, "XOR", 2, 1),
        new(0x19, "NOT", 1, 1),
        new(0x1a, "BYTE", 2, 1),
        new(0x1b, "SHL", 2, 1),
        new(0x1c, "SHR", 2, 1),
        new(0x1d, "SAR", 2, 1),
        new(0x1e, "CLZ", 1, 1),
        new(0x20, "KECCAK256", 2, 1),
        new(0x30, "ADDRESS", 0, 1),
        new(0x31, "BALANCE", 1, 1),
        new(0x32, "ORIGIN", 0, 1),
        new(0x33, "CALLER", 0, 1),
        new(0x34, "CALLVALUE", 0, 1),
        new(0x35, "CALLDATALOAD", 1, 1),
        new(0x36, "CALLDATASIZE", 0, 1),
        new(0x37, "CALLDATACOPY", 3, 0),
        new(0x38, "CODESIZE", 0, 1),
        new(0x39, "CODECOPY", 3, 0),
        new(0x3a, "GASPRICE", 0, 1),
        new(0x3b, "EXTCODESIZE", 1, 1),
        new(0x3c, "EXTCODECOPY", 4, 0),
        new(0x3d, "RETURNDATASIZE", 0, 1),
        new(0x3e, "RETURNDATACOPY", 3, 0),
        new(0x3f, "EXTCODEHASH", 1, 1),
        new(0x40, "BLOCKHASH", 1, 1),
        new(0x41, "COINBASE", 0, 1),
        new(0x42, "TIMESTAMP", 0, 1),
        new(0x43, "NUMBER", 0, 1),
        new(0x44, "DIFFICULTY", 0, 1),
        new(0x45, "GASLIMIT", 0, 1),
        new(0x46, "CHAINID", 0, 1),
        new(0x47, "SELFBALANCE", 0, 1),
        new(0x48, "BASEFEE", 0, 1),
        new(0x49, "BLOBHASH", 1, 1),
        new(0x4a, "BLOBBASEFEE", 0, 1),
        new(0x4b, "SLOTNUM", 0, 1),
        new(0x50, "POP", 1, 0),
        new(0x51, "MLOAD", 1, 1),
        new(0x52, "MSTORE", 2, 0),
        new(0x53, "MSTORE8", 2, 0),
        new(0x54, "SLOAD", 1, 1),
        new(0x55, "SSTORE", 2, 0),
        new(0x56, "JUMP", 1, 0),
        new(0x57, "JUMPI", 2, 0),
        new(0x58, "PC", 0, 1),
        new(0x59, "MSIZE", 0, 1),
        new(0x5a, "GAS", 0, 1),
        new(0x5b, "JUMPDEST", 0, 0),
        new(0x5c, "TLOAD", 1, 1),
        new(0x5d, "TSTORE", 2, 0),
        new(0x5e, "MCOPY", 3, 0),
        new(0x5f, "PUSH0", 0, 1),
        new(0x60, "PUSH1", 0, 1),
        new(0x61, "PUSH2", 0, 1),
        new(0x62, "PUSH3", 0, 1),
        new(0x63, "PUSH4", 0, 1),
        new(0x64, "PUSH5", 0, 1),
        new(0x65, "PUSH6", 0, 1),
        new(0x66, "PUSH7", 0, 1),
        new(0x67, "PUSH8", 0, 1),
        new(0x68, "PUSH9", 0, 1),
        new(0x69, "PUSH10", 0, 1),
        new(0x6a, "PUSH11", 0, 1),
        new(0x6b, "PUSH12", 0, 1),
        new(0x6c, "PUSH13", 0, 1),
        new(0x6d, "PUSH14", 0, 1),
        new(0x6e, "PUSH15", 0, 1),
        new(0x6f, "PUSH16", 0, 1),
        new(0x70, "PUSH17", 0, 1),
        new(0x71, "PUSH18", 0, 1),
        new(0x72, "PUSH19", 0, 1),
        new(0x73, "PUSH20", 0, 1),
        new(0x74, "PUSH21", 0, 1),
        new(0x75, "PUSH22", 0, 1),
        new(0x76, "PUSH23", 0, 1),
        new(0x77, "PUSH24", 0, 1),
        new(0x78, "PUSH25", 0, 1),
        new(0x79, "PUSH26", 0, 1),
        new(0x7a, "PUSH27", 0, 1),
        new(0x7b, "PUSH28", 0, 1),
        new(0x7c, "PUSH29", 0, 1),
        new(0x7d, "PUSH30", 0, 1),
        new(0x7e, "PUSH31", 0, 1),
        new(0x7f, "PUSH32", 0, 1),
        new(0x80, "DUP1", 1, 2),
        new(0x81, "DUP2", 2, 3),
        new(0x82, "DUP3", 3, 4),
        new(0x83, "DUP4", 4, 5),
        new(0x84, "DUP5", 5, 6),
        new(0x85, "DUP6", 6, 7),
        new(0x86, "DUP7", 7, 8),
        new(0x87, "DUP8", 8, 9),
        new(0x88, "DUP9", 9, 10),
        new(0x89, "DUP10", 10, 11),
        new(0x8a, "DUP11", 11, 12),
        new(0x8b, "DUP12", 12, 13),
        new(0x8c, "DUP13", 13, 14),
        new(0x8d, "DUP14", 14, 15),
        new(0x8e, "DUP15", 15, 16),
        new(0x8f, "DUP16", 16, 17),
        new(0x90, "SWAP1", 2, 2),
        new(0x91, "SWAP2", 3, 3),
        new(0x92, "SWAP3", 4, 4),
        new(0x93, "SWAP4", 5, 5),
        new(0x94, "SWAP5", 6, 6),
        new(0x95, "SWAP6", 7, 7),
        new(0x96, "SWAP7", 8, 8),
        new(0x97, "SWAP8", 9, 9),
        new(0x98, "SWAP9", 10, 10),
        new(0x99, "SWAP10", 11, 11),
        new(0x9a, "SWAP11", 12, 12),
        new(0x9b, "SWAP12", 13, 13),
        new(0x9c, "SWAP13", 14, 14),
        new(0x9d, "SWAP14", 15, 15),
        new(0x9e, "SWAP15", 16, 16),
        new(0x9f, "SWAP16", 17, 17),
        new(0xa0, "LOG0", 2, 0),
        new(0xa1, "LOG1", 3, 0),
        new(0xa2, "LOG2", 4, 0),
        new(0xa3, "LOG3", 5, 0),
        new(0xa4, "LOG4", 6, 0),
        new(0xd0, "DATALOAD", -1, -1),
        new(0xd1, "DATALOADN", -1, -1),
        new(0xd2, "DATASIZE", -1, -1),
        new(0xd3, "DATACOPY", -1, -1),
        new(0xe0, "RJUMP", -1, -1),
        new(0xe1, "RJUMPI", -1, -1),
        new(0xe2, "RJUMPV", -1, -1),
        new(0xe3, "CALLF", -1, -1),
        new(0xe4, "RETF", -1, -1),
        new(0xe5, "JUMPF", -1, -1),
        new(0xe6, "DUPN", 1, 0),
        new(0xe7, "SWAPN", 2, 0),
        new(0xe8, "EXCHANGE", 2, 0),
        new(0xec, "EOFCREATE", -1, -1),
        new(0xee, "RETURNCONTRACT", -1, -1),
        new(0xf0, "CREATE", 3, 1),
        new(0xf1, "CALL", 7, 1),
        new(0xf2, "CALLCODE", 7, 1),
        new(0xf3, "RETURN", 2, 0),
        new(0xf4, "DELEGATECALL", 6, 1),
        new(0xf5, "CREATE2", 4, 1),
        new(0xf7, "RETURNDATALOAD", -1, -1),
        new(0xf8, "EXTCALL", -1, -1),
        new(0xf9, "EXTDELEGATECALL", -1, -1),
        new(0xfa, "STATICCALL", 6, 1),
        new(0xfb, "EXTSTATICCALL", -1, -1),
        new(0xfd, "REVERT", 2, 0),
        new(0xfe, "INVALID", 0, 0),
        new(0xff, "SELFDESTRUCT", 1, 0),
    ]);

    /// <summary>Describes the failure of <paramref name="tx"/> as it ran in <paramref name="blockContext"/>.</summary>
    /// <param name="error">The failure as the processor described it.</param>
    /// <returns>Whether <paramref name="text"/> holds the text to report; false keeps <paramref name="error"/>.</returns>
    public static bool TryDescribe(
        ITransactionProcessor transactionProcessor,
        Transaction tx,
        in BlockExecutionContext blockContext,
        EvmExceptionType failure,
        string error,
        CancellationToken token,
        out string text)
    {
        text = error;
        if (error != failure.GetEvmExceptionDescription())
            return false;

        switch (failure)
        {
            case EvmExceptionType.InvalidJumpDestination
                or EvmExceptionType.StaticCallViolation
                or EvmExceptionType.AccessViolation
                or EvmExceptionType.TransactionCollision
                or EvmExceptionType.InvalidCode:
                return true;
            case EvmExceptionType.BadInstruction
                or EvmExceptionType.StackUnderflow
                or EvmExceptionType.StackOverflow:
                return TryDescribeFailingOperation(transactionProcessor, tx, in blockContext, failure, token, ref text);
            default:
                return false;
        }
    }

    private static bool TryDescribeFailingOperation(
        ITransactionProcessor transactionProcessor,
        Transaction tx,
        in BlockExecutionContext blockContext,
        EvmExceptionType failure,
        CancellationToken token,
        ref string text)
    {
        FailingOperationTracer tracer = new();
        TransactionResult result;
        try
        {
            result = transactionProcessor.CallAndRestore(tx, in blockContext, tracer.WithCancellation(token));
        }
        catch (InsufficientBalanceException)
        {
            return false;
        }

        if (result.EvmExceptionType != failure || tracer.Opcode is not { } opcode || HasOperandDependentFailure(opcode))
            return false;

        Operation? operation = Operations[opcode];
        switch (failure)
        {
            case EvmExceptionType.BadInstruction:
                text = operation is null ? $"invalid opcode: opcode 0x{opcode:x} not defined" : $"invalid opcode: {operation.Value.Name}";
                return true;
            case EvmExceptionType.StackUnderflow when operation is { Pops: not NotOnTheStackTable } known:
                text = $"stack underflow ({tracer.StackLength} <=> {known.Pops})";
                return true;
            case EvmExceptionType.StackOverflow when operation is { Pops: not NotOnTheStackTable } known:
                text = $"stack limit reached {tracer.StackLength} ({StackLimit + known.Pops - known.Pushes})";
                return true;
            default:
                return false;
        }
    }

    /// <summary>DUPN, SWAPN and EXCHANGE fail on their immediate, which the text would have to decode.</summary>
    private static bool HasOperandDependentFailure(byte opcode) => opcode is 0xe6 or 0xe7 or 0xe8;

    private static Operation?[] BuildOperations(ReadOnlySpan<Operation> operations)
    {
        Operation?[] table = new Operation?[256];
        foreach (Operation operation in operations)
            table[operation.Code] = operation;

        return table;
    }

    private readonly record struct Operation(byte Code, string Name, int Pops, int Pushes);

    /// <summary>Records the last operation the outermost frame started and the stack depth it started with.</summary>
    private sealed class FailingOperationTracer : TxTracer
    {
        private bool _inOutermostFrame;

        public override bool IsTracingInstructions => true;
        public override bool IsTracingStack => true;

        public byte? Opcode { get; private set; }
        public int StackLength { get; private set; }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            _inOutermostFrame = env.CallDepth == 0;
            if (_inOutermostFrame)
            {
                Opcode = (byte)opcode;
                StackLength = 0;
            }
        }

        public override void SetOperationStack(TraceStack stack)
        {
            if (_inOutermostFrame)
                StackLength = stack.Count;
        }
    }
}
