// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Eez.Follower;

/// <summary>
/// The L2 heads the follower owns: <see cref="Safe"/> is the last block L1 settled, <see cref="Finalized"/> the last one
/// settled in a finalized L1 block. The head itself is the last derived block, which is safe unless a partial settlement
/// left derived blocks above it.
/// </summary>
public sealed class FollowerHeads(BlockHeader safe, BlockHeader finalized)
{
    public BlockHeader Safe { get; set; } = safe;

    public BlockHeader Finalized { get; set; } = finalized;
}
