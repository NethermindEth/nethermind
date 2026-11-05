// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV2Result<TVersionedExecutionPayload>(Block block, UInt256 blockFees)
    where TVersionedExecutionPayload : ExecutionPayload, IExecutionPayloadParams, IExecutionPayloadFactory<TVersionedExecutionPayload>
{
    private TVersionedExecutionPayload? _executionPayload;

    protected internal Block Block { get; } = block;

    public UInt256 BlockValue { get; } = blockFees;

    public virtual TVersionedExecutionPayload ExecutionPayload => _executionPayload ??= TVersionedExecutionPayload.Create(Block);

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}}}";
}

public class GetPayloadV2Result(Block block, UInt256 blockFees) : GetPayloadV2Result<ExecutionPayload>(block, blockFees);
