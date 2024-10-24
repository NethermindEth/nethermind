// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public interface IPayloadPreparationService : IDisposable
    {
        string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes);

        ValueTask<IBlockProductionContext?> GetPayload(string payloadId);
        CancellationTokenSource CancelOngoingImprovements();

        event EventHandler<BlockEventArgs>? BlockImproved;
    }
}
