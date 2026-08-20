// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// A single frame of an EIP-8141 frame transaction: <c>[mode, flags, target, limits, value, data]</c>,
/// where <c>limits = [execution, state]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class TxFrame(byte mode, byte flags, Address? target, ulong executionGasLimit, ulong stateGasLimit, UInt256 value, ReadOnlyMemory<byte> data)
{
    public const byte ModeDefault = 0;
    public const byte ModeVerify = 1;
    public const byte ModeSender = 2;

    /// <summary>EIP-7906: a read-only trailing frame that asserts the transaction's outcome.</summary>
    public const byte ModePostTx = 3;

    public const byte ApproveScopeNone = 0x0;
    public const byte ApprovePayment = 0x1;
    public const byte ApproveExecution = 0x2;
    public const byte ApproveExecutionAndPayment = 0x3;
    public const byte ApproveScopeMask = ApproveExecutionAndPayment;
    public const byte AtomicBatchFlag = 0x4;

    /// <summary>Constructs a frame whose entire budget is execution gas, with <c>limits.state == 0</c>.</summary>
    public TxFrame(byte mode, byte flags, Address? target, ulong gasLimit, UInt256 value, ReadOnlyMemory<byte> data)
        : this(mode, flags, target, gasLimit, 0, value, data)
    {
    }

    public byte Mode { get; } = mode;
    public byte Flags { get; } = flags;

    /// <summary>Null resolves to the transaction sender during execution.</summary>
    public Address? Target { get; } = target;

    /// <summary>EIP-8141 <c>limits.execution</c>: the frame's execution-gas budget.</summary>
    public ulong ExecutionGasLimit { get; } = executionGasLimit;

    /// <summary>EIP-8141 <c>limits.state</c>: the frame's state-gas budget (EIP-8037).</summary>
    public ulong StateGasLimit { get; } = stateGasLimit;

    /// <summary>The combined gas the frame reserves against the payer, <c>limits.execution + limits.state</c>.</summary>
    /// <remarks>Static validation rejects a transaction whose frame reservations overflow, so this sum never
    /// wraps for a frame reaching execution.</remarks>
    public ulong GasLimit => ExecutionGasLimit + StateGasLimit;

    public UInt256 Value { get; } = value;
    public ReadOnlyMemory<byte> Data { get; } = data;

    public byte AllowedApproveScope => (byte)(Flags & ApproveScopeMask);
    public bool IsAtomicBatch => (Flags & AtomicBatchFlag) != 0;
}
