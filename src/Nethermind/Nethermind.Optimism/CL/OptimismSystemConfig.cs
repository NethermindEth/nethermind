// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public sealed record OptimismSystemConfig
{
    public required Address BatcherAddr { get; init; }
    public required byte[] Overhead { get; init; }
    public required byte[] Scalar { get; init; }
    public required ulong GasLimit { get; init; }
    [JsonPropertyName("eip1559Params")]
    public required byte[] EIP1559Params { get; init; }
    public required byte[] OperatorFeeParams { get; init; }
}
