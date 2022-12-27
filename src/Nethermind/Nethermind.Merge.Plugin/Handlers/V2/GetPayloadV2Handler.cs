// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data.V2;

namespace Nethermind.Merge.Plugin.Handlers.V2
{
    public class GetPayloadV2Handler : IAsyncHandler<byte[], GetPayloadV2Result?>
    {
        private readonly IPayloadPreparationService _payloadPreparationService;
        private readonly ILogger _logger;

        public GetPayloadV2Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager)
        {
            _payloadPreparationService = payloadPreparationService;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<GetPayloadV2Result?>> HandleAsync(byte[] payloadId)
        {
            string payloadStr = payloadId.ToHexString(true);
            IBlockProductionContext? blockContext = await _payloadPreparationService.GetPayload(payloadStr);
            Block? block = blockContext?.CurrentBestBlock;

            if (block is null)
            {
                // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
                if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
                return ResultWrapper<GetPayloadV2Result?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
            }

            if (_logger.IsInfo) _logger.Info($"GetPayloadV2 result: {block.Header.ToString(BlockHeader.Format.Full)}.");

            Metrics.GetPayloadRequests++;
            Metrics.NumberOfTransactionsInGetPayload = block.Transactions.Length;
            return ResultWrapper<GetPayloadV2Result?>.Success(new GetPayloadV2Result(block, blockContext!.BlockFees));
        }
    }
}
