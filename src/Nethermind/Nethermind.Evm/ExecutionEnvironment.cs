// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Execution environment for EVM calls. Embedded directly in <see cref="CallFrame{TGasPolicy}"/>
/// to eliminate a pointer chase on every opcode that accesses environment fields.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ExecutionEnvironment
{
    /// <summary>
    /// Parsed bytecode for the current call.
    /// </summary>
    public readonly CodeInfo? CodeInfo;

    /// <summary>
    /// Currently executing account (in DELEGATECALL this will be equal to caller).
    /// </summary>
    public readonly Address ExecutingAccount;

    /// <summary>
    /// Caller
    /// </summary>
    public readonly Address Caller;

    /// <summary>
    /// Bytecode source (account address).
    /// </summary>
    public readonly Address? CodeSource;

    /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
    public readonly int CallDepth;

    /// <summary>
    /// ETH value transferred in this call.
    /// </summary>
    public readonly UInt256 TransferValue;

    /// <summary>
    /// Value information passed (it is different from transfer value in DELEGATECALL).
    /// DELEGATECALL behaves like a library call, and it uses the value information from the caller even
    /// as no transfer happens.
    /// </summary>
    public readonly UInt256 Value;

    /// <summary>
    /// Parameters / arguments of the current call.
    /// </summary>
    public readonly ReadOnlyMemory<byte> InputData;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecutionEnvironment(
        CodeInfo? codeInfo,
        Address executingAccount,
        Address caller,
        Address? codeSource,
        int callDepth,
        in UInt256 transferValue,
        in UInt256 value,
        in ReadOnlyMemory<byte> inputData)
    {
        CodeInfo = codeInfo;
        ExecutingAccount = executingAccount;
        Caller = caller;
        CodeSource = codeSource;
        CallDepth = callDepth;
        TransferValue = transferValue;
        Value = value;
        InputData = inputData;
    }
}
