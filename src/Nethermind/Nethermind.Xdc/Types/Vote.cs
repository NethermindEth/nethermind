// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

public class Vote(BlockRoundInfo proposedBlockInfo, ulong gapNumber, Signature signature = null)
{
    public BlockRoundInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public ulong GapNumber { get; set; } = gapNumber;
    public Signature? Signature { get; set; } = signature;
    public Address? Signer { get; set; }

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.BlockNumber}";
}
