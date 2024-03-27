// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;
/// <summary>
///  Represents an object mapping the <c>InclusionListSummary</c> structure of the beacon chain spec.
/// </summary>
public class InclusionListSummary
{

    public InclusionListSummary(ulong slot, ulong proposerIndex, ulong nonce, Hash256 hash, InclusionListSummaryEntry[] summary)
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
    public InclusionListSummaryEntry[] Summary { get; set; }
}