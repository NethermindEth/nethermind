// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public class L1Origin(UInt256 blockID, Hash256? l2BlockHash, long l1BlockHeight, Hash256 l1BlockHash)
{
    public UInt256 BlockID { get; set; } = blockID;
    public Hash256? L2BlockHash { get; set; } = l2BlockHash;
    public long L1BlockHeight { get; set; } = l1BlockHeight;
    public Hash256 L1BlockHash { get; set; } = l1BlockHash;
}
