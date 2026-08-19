// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 frame: <c>[mode, flags, target, gas_limit, value, data]</c>.</summary>
public class FrameForRpc
{
    public byte Mode { get; set; }
    public byte Flags { get; set; }
    public Address? Target { get; set; }
    public ulong GasLimit { get; set; }
    public UInt256 Value { get; set; }
    public byte[] Data { get; set; } = [];

    [JsonConstructor]
    public FrameForRpc() { }

    public FrameForRpc(TxFrame frame)
    {
        Mode = frame.Mode;
        Flags = frame.Flags;
        Target = frame.Target;
        GasLimit = frame.GasLimit;
        Value = frame.Value;
        Data = frame.Data.ToArray();
    }

    public TxFrame ToFrame() => new(Mode, Flags, Target, GasLimit, Value, Data);

    public static FrameForRpc[]? FromFrames(TxFrame[]? frames) =>
        frames?.Select(static f => new FrameForRpc(f)).ToArray();

    /// <inheritdoc cref="RpcListConverter.TryConvert{TView,TValue}"/>
    public static bool TryToFrames(FrameForRpc[]? frames, out TxFrame[]? converted) =>
        RpcListConverter.TryConvert(frames, static f => f.ToFrame(), out converted);

    /// <summary>The gas limits of <paramref name="frames"/>, saturating at <see cref="ulong.MaxValue"/>.</summary>
    /// <remarks>
    /// This is what an EIP-8141 transaction reserves and spends; <see cref="Transaction.GasLimit"/> carries the
    /// request's <c>gas</c> field, which the frame path never reads.
    /// </remarks>
    public static ulong TotalGasLimit(FrameForRpc[]? frames)
    {
        ulong total = 0;
        foreach (FrameForRpc frame in frames ?? [])
        {
            if (frame.GasLimit > ulong.MaxValue - total) return ulong.MaxValue;
            total += frame.GasLimit;
        }

        return total;
    }
}
