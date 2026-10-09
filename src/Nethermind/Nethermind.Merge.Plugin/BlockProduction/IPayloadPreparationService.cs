// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public interface IPayloadPreparationService
    {
        string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes);

        /// <summary>Returns an immutable snapshot of the best candidate, or <c>null</c> for an unknown payload.</summary>
        ValueTask<IBlockProductionContext?> GetPayload(string payloadId, bool skipCancel = false);
        void CancelBlockProduction(string payloadId);
    }
}
