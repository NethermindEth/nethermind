// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class PublicApiSnapshot
{
    /// <summary>
    /// Block number where the snapshot was created
    /// </summary>
    public ulong Number { get; set; }

    /// <summary>
    /// Block hash where the snapshot was created
    /// </summary>
    public Hash256 Hash { get; set; }

    /// <summary>
    /// Set of authorized signers at this moment
    /// </summary>
    public HashSet<Address> Signers { get; set; }

    /// <summary>
    /// Set of recent signers for spam protections
    /// </summary>
    public Dictionary<ulong, Address> Recents { get; set; }

    /// <summary>
    /// List of votes cast in chronological order
    /// </summary>
    public Vote[] Votes { get; set; }

    /// <summary>
    /// Current vote tally to avoid recalculating
    /// </summary>
    public Dictionary<Address, Tally> Tally { get; set; }
}
