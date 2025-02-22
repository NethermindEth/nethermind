// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers;

public interface IBlockImprovementContextFactory
{
    IBlockImprovementContext StartBlockImprovementContext(
        Block currentBestBlock,
        BlockHeader parentHeader,
        PayloadAttributes payloadAttributes,
        DateTimeOffset startDateTime,
        CancellationToken cancellationToken = default);
}
