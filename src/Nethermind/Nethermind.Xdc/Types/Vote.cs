// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Text.Json.Serialization;

namespace Nethermind.Xdc.Types;

public class Vote(BlockInfo proposedBlockInfo, ulong gapNumber, Signature? signature = null)
{
    private Address Signer { set; get; }
    public BlockInfo ProposedBlockInfo { get; set; } = proposedBlockInfo;
    public Signature? Signature { get; set; } = signature;
    public ulong GapNumber { get; set; } = gapNumber;

    public override string ToString() =>
        $"{ProposedBlockInfo.Round}:{GapNumber}:{ProposedBlockInfo.Number}";
}
