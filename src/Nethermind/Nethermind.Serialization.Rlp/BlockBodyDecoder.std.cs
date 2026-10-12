// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public sealed partial class BlockBodyDecoder
{
    public static readonly RlpLimit UnclesCountLimit = RlpLimit.For<BlockBody>(2, nameof(BlockBody.Uncles));

    // Actual consensus-level max is 16, see MAX_WITHDRAWALS_PER_PAYLOAD at https://github.com/ethereum/consensus-specs/blob/master/specs/capella/beacon-chain.md
    // Increased here for compatibility with execution spec tests and benchmarks
    public static readonly RlpLimit WithdrawalsCountLimit = RlpLimit.For<BlockBody>(64_000, nameof(BlockBody.Withdrawals));
}
