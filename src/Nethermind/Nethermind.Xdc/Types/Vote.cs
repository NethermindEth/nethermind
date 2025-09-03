// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

public class Vote(Address signer, BlockInfo proposedBlockInfo, Signature signature, long gapNumber)
{
    public Vote(BlockInfo proposedBlockInfo, Signature signature, long gapNumber)
        : this(default, proposedBlockInfo, signature, gapNumber)
    {
    }

    private Address _signer = signer;
    public BlockInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public Signature Signature { get; set; } = signature;
    public long GapNumber { get; set; } = gapNumber;

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.Number}:{ProposedBlockInfo.SigHash()}";

    public Address GetSigner() => _signer;
    public void SetSigner(Address signer) => _signer = signer;
}
