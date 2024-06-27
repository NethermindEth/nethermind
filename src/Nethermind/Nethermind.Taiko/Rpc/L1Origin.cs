// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko.Rpc;

public class L1Origin(ulong blockId, Hash256? l2BlockHash, UInt256 l1BlockHeight, Hash256 l1BlockHash)
{
    public ulong BlockId { get; set; } = blockId;
    public Hash256? L2BlockHash { get; set; } = l2BlockHash;
    public UInt256 L1BlockHeight { get; set; } = l1BlockHeight;
    public Hash256 L1BlockHash { get; set; } = l1BlockHash;
}
