// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>rollup0's DA: the EEZ stream in calldata, one flat action per effect, rebuilt by <see cref="BatchDerivation"/>.</summary>
public sealed class Rollup0BatchDerivation(EezSettlementContext context) : IBatchDerivation
{
    public DecodedBatch Decode(ReadOnlyMemory<byte> daStream)
    {
        DaPayload payload = DaPayloadCodec.Decode(daStream);
        return payload.RollupId == context.RollupId && payload.Span.BlockCount != 0
            ? new Decoded(payload, context)
            : throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload,
                $"the DA is for rollup {payload.RollupId} and covers {payload.Span.BlockCount} blocks");
    }

    private sealed class Decoded(DaPayload payload, EezSettlementContext context) : DecodedBatch
    {
        public override ulong BlockCount => (ulong)payload.Span.BlockCount;

        public override DerivedBlock[] Derive(ProducingSlice applied, in DerivationInputs inputs) =>
            BatchDerivation.Derive(payload, applied, inputs.SystemNonce, context.ChainId, context.RollupId);
    }
}
