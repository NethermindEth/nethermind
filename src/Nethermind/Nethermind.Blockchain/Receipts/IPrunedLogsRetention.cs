// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts;

/// <summary>
/// Whether logs for a set of addresses are still answerable over a block range the general history pruner has
/// reclaimed. An implementation may only answer true when it retains the receipts of every block in the range that
/// has a log for any of the addresses - a bloom-gated scan reads a superset of those blocks, and a read that finds
/// no receipts for a block with no matching log contributes exactly the empty set it would have contributed. That
/// includes depths the node never stored at all: receipts that were never downloaded, or reclaimed before the
/// retention was configured, are not retained by anyone and must refuse.
/// </summary>
public interface IPrunedLogsRetention
{
    /// <summary>True only when every address is retained over the whole of <c>[fromBlock, toBlock]</c>.</summary>
    bool RetainsLogsFor(IReadOnlyCollection<AddressAsKey> addresses, ulong fromBlock, ulong toBlock);
}
