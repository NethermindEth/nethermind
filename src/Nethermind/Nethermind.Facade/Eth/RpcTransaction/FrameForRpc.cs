// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 frame: <c>[mode, flags, target, limits, value, data]</c>,
/// where <c>limits = [execution, state]</c>.</summary>
public class FrameForRpc
{
    /// <summary>The frame's kind, one of the <see cref="TxFrame"/> <c>Mode*</c> values: <c>0</c> default,
    /// <c>1</c> verify, <c>2</c> sender, <c>3</c> post-tx.</summary>
    /// <remarks>It fixes both the caller the frame runs as and where the frame may appear in the transaction.</remarks>
    public byte Mode { get; set; }

    /// <summary>The frame's flag byte: bits 0-1 carry the approval scope
    /// (<see cref="TxFrame.ApprovePayment"/> | <see cref="TxFrame.ApproveExecution"/>), bit 2 is
    /// <see cref="TxFrame.AtomicBatchFlag"/>.</summary>
    public byte Flags { get; set; }

    /// <summary>The frame's target address; omitted from the response, and accepted as absent in a request,
    /// when the frame targets the transaction sender.</summary>
    public Address? Target { get; set; }

    /// <summary>EIP-8141 <c>limits.execution</c>: the frame's execution-gas budget, in gas.</summary>
    public ulong ExecutionGasLimit { get; set; }

    /// <summary>EIP-8141 <c>limits.state</c>: the frame's EIP-8037 state-gas budget, in gas.</summary>
    /// <remarks>The payer reserves the two budgets summed; neither is spendable as the other.</remarks>
    public ulong StateGasLimit { get; set; }

    /// <summary>Wei the frame moves from its caller to <see cref="Target"/>.</summary>
    public UInt256 Value { get; set; }

    /// <summary>The frame's calldata.</summary>
    public byte[] Data { get; set; } = [];

    [JsonConstructor]
    public FrameForRpc() { }

    public FrameForRpc(TxFrame frame)
    {
        Mode = frame.Mode;
        Flags = frame.Flags;
        Target = frame.Target;
        ExecutionGasLimit = frame.ExecutionGasLimit;
        StateGasLimit = frame.StateGasLimit;
        Value = frame.Value;
        Data = frame.Data.ToArray();
    }

    public TxFrame ToFrame() => new(Mode, Flags, Target, ExecutionGasLimit, StateGasLimit, Value, Data);

    public static FrameForRpc[]? FromFrames(TxFrame[]? frames)
    {
        if (frames is null) return null;

        FrameForRpc[] result = new FrameForRpc[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            result[i] = new FrameForRpc(frames[i]);
        }

        return result;
    }

    /// <summary>Maps the deserialized <c>frames</c> list onto the transaction's frames.</summary>
    /// <param name="frames">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="frames"/> is absent.</param>
    /// <returns><c>false</c> if any element was JSON <c>null</c>.</returns>
    public static bool TryToFrames(FrameForRpc[]? frames, out TxFrame[]? converted) =>
        RpcListConverter.TryConvert(frames, static f => f.ToFrame(), out converted);
}
