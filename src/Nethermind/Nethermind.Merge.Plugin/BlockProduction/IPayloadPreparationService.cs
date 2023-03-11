// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public interface IPayloadPreparationService
    {
        string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes);

        ValueTask<IBlockProductionContext?> GetPayload(string payloadId);

        event EventHandler<BlockEventArgs>? BlockImproved;
    }
}
