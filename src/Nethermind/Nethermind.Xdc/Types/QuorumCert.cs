// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

public class QuorumCert
{
    public QuorumCert(BlockInfo proposedBlockInfo, Signature[] signatures, long gapNumber)
    {
        ProposedBlockInfo = proposedBlockInfo;
        Signatures = signatures;
        GapNumber = gapNumber;
    }

    [JsonPropertyName("proposedBlockInfo")]
    public BlockInfo ProposedBlockInfo { get; set; }

    [JsonPropertyName("signatures")]
    public Signature[] Signatures { get; set; }

    [JsonPropertyName("gapNumber")]
    public long GapNumber { get; set; }
}
