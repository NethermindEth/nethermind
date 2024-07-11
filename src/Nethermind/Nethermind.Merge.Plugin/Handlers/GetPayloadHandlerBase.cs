// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public abstract class GetPayloadHandlerBase<TGetPayloadResult> : IAsyncHandler<byte[], TGetPayloadResult?> where TGetPayloadResult : IForkValidator
{
    private readonly int _apiVersion;
    private readonly IPayloadPreparationService _payloadPreparationService;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    protected GetPayloadHandlerBase(int apiVersion, IPayloadPreparationService payloadPreparationService,
        ISpecProvider specProvider, ILogManager logManager)
    {
        _apiVersion = apiVersion;
        _payloadPreparationService = payloadPreparationService;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<TGetPayloadResult?>> HandleAsync(byte[] payloadId)
    {
        string payloadStr = payloadId.ToHexString(true);
        IBlockProductionContext? blockContext = await _payloadPreparationService.GetPayload(payloadStr);
        Block? block = blockContext?.CurrentBestBlock;

        if (blockContext is null || block is null)
        {
            // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
            if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
            return ResultWrapper<TGetPayloadResult?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
        }

        TGetPayloadResult getPayloadResult = GetPayloadResultFromBlock(blockContext);

        if (!getPayloadResult.ValidateFork(_specProvider))
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<TGetPayloadResult?>.Fail("unsupported fork", MergeErrorCodes.UnsupportedFork);
        }

        if (_logger.IsInfo) _logger.Info($"GetPayloadV{_apiVersion} result: {block.Header.ToString(BlockHeader.Format.Full)}.");

        Metrics.GetPayloadRequests++;
        Metrics.NumberOfTransactionsInGetPayload = block.Transactions.Length;

        if (_logger.IsWarn) _logger.Warn($"Get payload. Blocks's hash: {block.Hash}, txroot: {block.TxRoot}, wroot: {block.WithdrawalsRoot}, state: {block.StateRoot}");
        if (_logger.IsWarn) _logger.Warn($"Get payload. Payload's hash: {(getPayloadResult as GetPayloadV2Result)?.ExecutionPayload.BlockHash} wroot: {(getPayloadResult as GetPayloadV2Result)?.ExecutionPayload.Withdrawals}, state: {(getPayloadResult as GetPayloadV2Result)?.ExecutionPayload.Transactions}");
        if ((getPayloadResult as GetPayloadV2Result)?.ExecutionPayload.TryGetBlock(out Block? b) == true)
        {
            if (_logger.IsWarn) _logger.Warn($"Get payload. Payload blocks's hash: {b?.Hash} wroot: {b?.WithdrawalsRoot}, txroot: {b?.TxRoot}, state: {b?.StateRoot}");
        }

        return ResultWrapper<TGetPayloadResult?>.Success(getPayloadResult);
    }

    protected abstract TGetPayloadResult GetPayloadResultFromBlock(IBlockProductionContext blockProductionContext);
}
