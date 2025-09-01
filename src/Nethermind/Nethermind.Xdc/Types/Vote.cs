// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Text.Json.Serialization;

namespace Nethermind.Consensus.HotStuff.Types;

public class Vote
{
    private Address _signer;

    public Vote(Address signer, BlockInfo proposedBlockInfo, Signature signature, long gapNumber)
    {
        _signer = signer;
        ProposedBlockInfo = proposedBlockInfo;
        Signature = signature;
        GapNumber = gapNumber;
    }

    public Vote(BlockInfo proposedBlockInfo, Signature signature, long gapNumber)
    {
        _signer = default;
        ProposedBlockInfo = proposedBlockInfo;
        Signature = signature;
        GapNumber = gapNumber;
    }

    [JsonPropertyName("proposedBlockInfo")]
    public BlockInfo ProposedBlockInfo { get; set; }

    [JsonPropertyName("signature")]
    public Signature Signature { get; set; }

    [JsonPropertyName("gapNumber")]
    public long GapNumber { get; set; }

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.Number}:{ProposedBlockInfo.Hash()}";

    public Address GetSigner() => _signer;
    public void SetSigner(Address signer) => _signer = signer;
}
