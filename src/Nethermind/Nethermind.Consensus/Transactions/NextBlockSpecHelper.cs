// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions;
internal static class NextBlockSpecHelper
{
    public static IReleaseSpec GetSpec(ISpecProvider specProvider, BlockHeader parentHeader,
        PayloadAttributes? payloadAttributes, IBlocksConfig? blocksConfig)
        => specProvider.GetSpec(parentHeader.Number + 1, payloadAttributes?.Timestamp ?? parentHeader.Timestamp + (blocksConfig?.SecondsPerSlot ?? 0));
}
