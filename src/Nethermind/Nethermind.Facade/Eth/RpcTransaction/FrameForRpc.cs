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
    public byte Mode { get; set; }
    public byte Flags { get; set; }
    public Address? Target { get; set; }
    public ulong ExecutionGasLimit { get; set; }
    public ulong StateGasLimit { get; set; }
    public UInt256 Value { get; set; }
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

    /// <summary>The gas limits of <paramref name="frames"/>, saturating at <see cref="ulong.MaxValue"/>.</summary>
    /// <remarks>
    /// This is what an EIP-8141 transaction reserves and spends, and what <see cref="Transaction.GasLimit"/>
    /// carries for one. Takes the converted frames, which are null-free by construction.
    /// </remarks>
    public static ulong TotalGasLimit(TxFrame[]? frames)
    {
        ulong total = 0;
        foreach (TxFrame frame in frames ?? [])
        {
            if (frame.GasLimit > ulong.MaxValue - total) return ulong.MaxValue;
            total += frame.GasLimit;
        }

        return total;
    }
}
