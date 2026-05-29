// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
    /// Recent signers for spam protection (V1 snapshots).
    /// </summary>
    [JsonPropertyName("recents")]
    public Dictionary<ulong, Address>? Recents { get; set; }

    /// <summary>
    /// Votes cast in chronological order (V1 snapshots).
    /// </summary>
    [JsonPropertyName("votes")]
    public object[]? Votes { get; set; }

    /// <summary>
    /// Current vote tally (V1 snapshots).
    /// </summary>
    [JsonPropertyName("tally")]
    public Dictionary<Address, object>? Tally { get; set; }
}
