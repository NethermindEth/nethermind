// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts;

/// <summary>
/// Whether logs for a set of addresses are still answerable over a block range the general history pruner has
/// reclaimed. An implementation may only answer true when it retains the receipts of every block in the range whose
/// bloom matches any of the addresses - a log scan skips the other blocks without reading them, so that is exactly
/// the set the scan will ask for.
/// </summary>
public interface IPrunedLogsRetention
{
    /// <summary>True only when every address is retained over the whole of <c>[fromBlock, toBlock]</c>.</summary>
    bool RetainsLogsFor(IReadOnlyCollection<AddressAsKey> addresses, ulong fromBlock, ulong toBlock);
}
