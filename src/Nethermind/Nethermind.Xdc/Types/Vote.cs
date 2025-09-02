// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

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

    public BlockInfo ProposedBlockInfo { get; set; }
    public Signature Signature { get; set; }
    public long GapNumber { get; set; }

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.Number}:{ProposedBlockInfo.SigHash()}";

    public Address GetSigner() => _signer;
    public void SetSigner(Address signer) => _signer = signer;
}
