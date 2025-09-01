// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class VoteForSign
{
    public VoteForSign(BlockInfo proposedBlockInfo, long gapNumber)
    {
        ProposedBlockInfo = proposedBlockInfo;
        GapNumber = gapNumber;
    }

    public BlockInfo ProposedBlockInfo { get; set; }
    public long GapNumber { get; set; }
    public Hash256 Hash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
