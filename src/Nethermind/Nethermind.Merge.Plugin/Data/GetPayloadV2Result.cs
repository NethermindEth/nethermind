// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV2Result : IValidateFork
{
    public GetPayloadV2Result(Block block, UInt256 blockFees)
    {
        BlockValue = blockFees;
        ExecutionPayload = new(block);
    }

    public UInt256 BlockValue { get; }

    public virtual ExecutionPayload ExecutionPayload { get; }

    public bool ValidateFork(ISpecProvider specProvider) => ExecutionPayload.ValidateFork(specProvider);

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}}}";
}
