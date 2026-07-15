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

    public static TxFrame[]? ToFrames(FrameForRpc[]? frames) =>
        frames?.Select(static f => f.ToFrame()).ToArray();
}
