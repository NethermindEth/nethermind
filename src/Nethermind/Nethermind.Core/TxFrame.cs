// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// A single frame of an EIP-8141 frame transaction: <c>[mode, flags, target, gas_limit, value, data]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class TxFrame(byte mode, byte flags, Address? target, ulong gasLimit, UInt256 value, ReadOnlyMemory<byte> data)
{
    public const byte ModeDefault = 0;
    public const byte ModeVerify = 1;
    public const byte ModeSender = 2;

    /// <summary>EIP-8288 dependency-verification frame; declares dependencies, not executed as EVM code.</summary>
    public const byte ModeDepVerify = Eip8288Constants.DepVerifyFrameMode;

    public const byte ApproveScopeNone = 0x0;
    public const byte ApprovePayment = 0x1;
    public const byte ApproveExecution = 0x2;
    public const byte ApproveExecutionAndPayment = 0x3;
    public const byte ApproveScopeMask = ApproveExecutionAndPayment;
    public const byte AtomicBatchFlag = 0x4;

    public byte Mode { get; } = mode;
    public byte Flags { get; } = flags;

    /// <summary>Null resolves to the transaction sender during execution.</summary>
    public Address? Target { get; } = target;

    public ulong GasLimit { get; } = gasLimit;
    public UInt256 Value { get; } = value;
    public ReadOnlyMemory<byte> Data { get; } = data;

    public byte AllowedApproveScope => (byte)(Flags & ApproveScopeMask);
    public bool IsAtomicBatch => (Flags & AtomicBatchFlag) != 0;
}
