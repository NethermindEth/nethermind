// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Consensus.HotStuff.Types;

public class QuorumCert
{
    [JsonPropertyName("proposedBlockInfo")]
    public BlockInfo ProposedBlockInfo { get; set; }

    [JsonPropertyName("signatures")]
    public Signature[] Signatures { get; set; }

    [JsonPropertyName("gapNumber")]
    public long GapNumber { get; set; }
}
