// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.Handlers;

public abstract class GetPayloadHandlerBase<TGetPayloadResult>(
    int apiVersion,
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager,
    IBuilderOverridePolicy? builderOverridePolicy = null)
    : IAsyncHandler<byte[], TGetPayloadResult?>
    where TGetPayloadResult : IForkValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadHandlerBase<>));

    /// <summary>The spec provider passed to this handler.</summary>
    protected ISpecProvider SpecProvider => specProvider;

    public async Task<ResultWrapper<TGetPayloadResult?>> HandleAsync(byte[] payloadId)
    {
        string payloadStr = payloadId.ToHexString(true);
        IBlockProductionContext? blockContext = await payloadPreparationService.GetPayload(payloadStr);
        // Fees before the block: improvement publishes the block first, so this order can only pair
        // a newer block with older fees, which under-reports blockValue rather than inflating it.
        UInt256 blockFees = blockContext?.BlockFees ?? default;
        Block? block = blockContext?.CurrentBestBlock;

        if (blockContext is null || block is null)
        {
            // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
            if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
            return ResultWrapper<TGetPayloadResult?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
        }

        // Freeze the candidate. Improvement runs concurrently, and a response that read the live
        // context could take its payload from one block and its blobs bundle from another.
        TGetPayloadResult getPayloadResult = GetPayloadResultFromBlock(new NoBlockProductionContext(block, blockFees));

        if (!getPayloadResult.ValidateFork(specProvider))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<TGetPayloadResult?>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        }

        if (_logger.IsInfo) _logger.Info($"GetPayloadV{apiVersion} result: {block.Header.ToString(BlockHeader.Format.Short)}.");

        Metrics.GetPayloadRequests++;
        Metrics.NumberOfTransactionsInGetPayload = block.Transactions.Length;
        return ResultWrapper<TGetPayloadResult?>.Success(getPayloadResult);
    }

    /// <summary>
    /// Evaluates policies used by Engine API getPayload V3 and later to populate the <c>shouldOverrideBuilder</c> field.
    /// </summary>
    protected bool ShouldOverrideBuilder(Block block) => builderOverridePolicy?.ShouldOverrideBuilder(block) ?? false;

    /// <summary>Builds the versioned response from the selected payload.</summary>
    /// <param name="blockProductionContext">
    /// An immutable snapshot of the selected block and its fees, safe to read repeatedly.
    /// </param>
    protected abstract TGetPayloadResult GetPayloadResultFromBlock(IBlockProductionContext blockProductionContext);
}
