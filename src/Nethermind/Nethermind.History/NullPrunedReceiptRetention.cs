// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.History;

public sealed class NullPrunedReceiptRetention : IPrunedReceiptRetention
{
    public static readonly NullPrunedReceiptRetention Instance = new();

    private NullPrunedReceiptRetention()
    {
    }

    public bool ShouldRetainReceipts(Block block) => false;
}
