// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.History;

/// <summary>
/// Decides whether a block the history pruner is about to delete must keep its receipts because something on this
/// node still answers queries for it. Keeps the pruner free of any one backend's retention rules: the default
/// implementation never retains, so a node that configures no such backend pays nothing and carries no coupling.
/// </summary>
public interface IPrunedReceiptRetention
{
    bool ShouldRetainReceipts(Block block);
}
