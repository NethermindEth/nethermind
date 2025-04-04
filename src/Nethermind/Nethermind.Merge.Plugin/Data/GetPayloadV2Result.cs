// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV2Result<TVersionedExecutionPayload>(Block block, UInt256 blockFees)
    : IForkValidator where TVersionedExecutionPayload : ExecutionPayload, IExecutionPayloadParams, IExecutionPayloadFactory<TVersionedExecutionPayload>
{
    public UInt256 BlockValue { get; } = blockFees;

    public virtual TVersionedExecutionPayload ExecutionPayload { get; } = TVersionedExecutionPayload.Create(block);

    public virtual bool ValidateFork(ISpecProvider specProvider) => ExecutionPayload.ValidateFork(specProvider);

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}}}";
}

public class GetPayloadV2Result(Block block, UInt256 blockFees) : GetPayloadV2Result<ExecutionPayload>(block, blockFees);
