// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public interface IBoostRelay
{
    Task<BoostPayloadAttributes> GetPayloadAttributes(PayloadAttributes payloadAttributes, CancellationToken cancellationToken);
    Task SendPayload(BoostExecutionPayloadV1 executionPayloadV1, CancellationToken cancellationToken);
}
