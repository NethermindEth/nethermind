// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.Handlers;

public abstract class GetPayloadHandlerBase<TGetPayloadResult>(
    int apiVersion,
    IPayloadPreparationService payloadPreparationService,
    ISpecProvider specProvider,
    ILogManager logManager,
    CensorshipDetector? censorshipDetector = null)
    : IAsyncHandler<byte[], TGetPayloadResult?>
    where TGetPayloadResult : IForkValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public async Task<ResultWrapper<TGetPayloadResult?>> HandleAsync(byte[] payloadId)
    {
        string payloadStr = payloadId.ToHexString(true);
        IBlockProductionContext? blockContext = await payloadPreparationService.GetPayload(payloadStr);
        Block? block = blockContext?.CurrentBestBlock;

        if (blockContext is null || block is null)
        {
            // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
            if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
            return ResultWrapper<TGetPayloadResult?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
        }

        TGetPayloadResult getPayloadResult = GetPayloadResultFromBlock(blockContext);

        if (!getPayloadResult.ValidateFork(specProvider))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<TGetPayloadResult?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
        }

        if (_logger.IsInfo) _logger.Info($"GetPayloadV{apiVersion} result: {block.Header.ToString(BlockHeader.Format.Short)}.");

        Metrics.GetPayloadRequests++;
        Metrics.NumberOfTransactionsInGetPayload = block.Transactions.Length;
        return ResultWrapper<TGetPayloadResult?>.Success(getPayloadResult);
    }

    protected bool ShouldOverrideBuilder(Block block)
         => censorshipDetector?.GetCensoredBlocks().Contains(new BlockNumberHash(block)) ?? false;

    protected abstract TGetPayloadResult GetPayloadResultFromBlock(IBlockProductionContext blockProductionContext);
}
