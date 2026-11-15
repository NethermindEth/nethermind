// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

public class AccountCollector : RangeQueryVisitor.ILeafValueCollector
{
    public SlimAccountRange Accounts { get; } = new();

    public int Collect(in ValueHash256 path, ReadOnlySpan<byte> value) => 32 + Accounts.AddLeaf(path, value);
}
