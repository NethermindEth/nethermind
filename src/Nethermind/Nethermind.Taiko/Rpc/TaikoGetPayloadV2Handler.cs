// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko.Rpc;

/// <summary>
/// Taiko-specific GetPayloadV2 handler. Overrides <see cref="GetPayloadV2Handler"/>
/// to skip fork validation (Taiko always uses V2) and to carry header difficulty
/// through blockValue for Unzen blocks (matches alethia-reth behavior).
/// </summary>
public class TaikoGetPayloadV2Handler(
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager)
    : GetPayloadHandlerBase<GetPayloadV2Result>(2, payloadPreparationService, specProvider, logManager)
{
    protected override GetPayloadV2Result GetPayloadResultFromBlock(IBlockProductionContext context)
    {
        Block block = context.CurrentBestBlock!;
        ITaikoReleaseSpec spec = (ITaikoReleaseSpec)SpecProvider.GetSpec(block.Header);

        // For Unzen, carry header difficulty through blockValue (matches alethia-reth behavior).
        // The driver reads blockValue from the standard ExecutionPayloadEnvelopeV2.
        UInt256 blockValue = spec.IsUnzenEnabled ? block.Difficulty : context.BlockFees;

        return new TaikoGetPayloadV2Result(block, blockValue);
    }
}

/// <summary>
/// Taiko-specific GetPayloadV2 result that uses <see cref="TaikoExecutionPayload"/>
/// and always passes fork validation since Taiko uses V2 regardless of EVM spec.
/// Header difficulty is carried solely through blockValue in the envelope,
/// matching alethia-reth behavior.
/// </summary>
public class TaikoGetPayloadV2Result(Block block, UInt256 blockFees) : GetPayloadV2Result(block, blockFees)
{
    private readonly TaikoExecutionPayload _taikoPayload = TaikoExecutionPayload.Create(block);

    public override ExecutionPayload ExecutionPayload => _taikoPayload;

    public override bool ValidateFork(ISpecProvider specProvider) => true;
}
