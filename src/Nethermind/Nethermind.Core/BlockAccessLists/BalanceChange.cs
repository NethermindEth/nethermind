// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct BalanceChange(uint Index, UInt256 Value) : IIndexedChange
{
    [JsonInclude]
    public readonly UInt256 Value = Value;

    public override string ToString() => $"{Index}:{Value}";
}
