// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

using Nethermind.Core.Crypto;


/// <summary>
///  Represents an object mapping the <c>InclusionListSummary</c> structure of the beacon chain spec.
/// </summary>
public class InclusionListSummaryV1
{

    public InclusionListSummaryV1(ulong slot, ulong proposerIndex, ulong nonce, Hash256 hash, InclusionListSummaryEntryV1[] summary)
    {
        Slot = slot;
        ProposerIndex = proposerIndex;
        Nonce = nonce;
        Hash = hash;
        Summary = summary;
    }

    public ulong Slot { get; set; }
    public ulong ProposerIndex { get; set; }
    public ulong Nonce { get; set; }
    public Hash256 Hash { get; set; }
    public InclusionListSummaryEntryV1[] Summary { get; set; }
}