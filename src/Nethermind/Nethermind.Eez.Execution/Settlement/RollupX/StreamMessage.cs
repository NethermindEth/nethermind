// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>One message of the EEZ blob stream between the leading <c>ChainOperation</c> and <c>CloseBlobStream</c>.</summary>
public abstract record StreamMessage
{
    /// <summary>Opens a cross-chain transaction that originates on <paramref name="ChainId"/>.</summary>
    public sealed record Initiate(ulong ChainId, ReadOnlyMemory<byte> TxData) : StreamMessage;

    public sealed record Call(ulong ToChain, Address From, Address To, UInt256 Value, ulong Gas, ReadOnlyMemory<byte> Data) : StreamMessage;

    public sealed record StaticCall(ulong ToChain, Address From, Address To, ulong Gas, ReadOnlyMemory<byte> Data) : StreamMessage;

    public sealed record Return(bool Success, ReadOnlyMemory<byte> ReturnData) : StreamMessage;

    public sealed record Snapshot : StreamMessage;

    public sealed record Revert : StreamMessage;

    public sealed record Finish : StreamMessage;
}
