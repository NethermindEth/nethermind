// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
internal class BlockInfo(JsonNode json) : IFormattable
{
    public long Number { get; } = Convert.ToInt64(json["number"]!.GetValue<string>(), 16);
    public string Hash { get; } = json["hash"]!.GetValue<string>();
    public long BaseFeePerGas { get; } = Convert.ToInt64(json["baseFeePerGas"]!.GetValue<string>(), 16);

    public override string ToString() => $"{Number} ({Hash})";

    public string ToString(string? format, IFormatProvider? formatProvider) => format?.Equals("#") == true
        ? Number.ToString()
        : ToString();
}
