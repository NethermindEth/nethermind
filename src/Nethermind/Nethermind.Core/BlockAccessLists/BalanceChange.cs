// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct BalanceChange(int Index, UInt256 Value) : IIndexedChange
{
    public override string ToString() => $"{Index}:{Value}";
}
