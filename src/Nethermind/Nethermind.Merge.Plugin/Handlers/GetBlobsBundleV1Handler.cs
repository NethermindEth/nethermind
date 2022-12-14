// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// engine_getBlobsBundleV1
///
/// Given a 8 byte payload_id, it returns blobs and kzgs of the most recent version of an execution payload
/// that is available by the time of the call or responds with an error.
/// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#engine_getblobsbundlev1">engine_getblobsbundlev1</a>
/// </summary>
public class GetBlobsBundleV1Handler : IAsyncHandler<byte[], BlobsBundleV1?>
{
    private readonly IPayloadPreparationService _payloadPreparationService;
    private readonly ILogger _logger;

    public GetBlobsBundleV1Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager)
    {
        _payloadPreparationService = payloadPreparationService;
        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<BlobsBundleV1?>> HandleAsync(byte[] payloadId)
    {
        string payloadStr = payloadId.ToHexString(true);
        Block? block = (await _payloadPreparationService.GetPayload(payloadStr))?.CurrentBestBlock;

        if (block is null)
        {
            // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
            return ResultWrapper<BlobsBundleV1?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
        }

        BlobsBundleV1 blobsBundle = new(block);
        if (_logger.IsInfo)
            _logger.Info(
                $"GetBlobsBundleV1 result: {blobsBundle.Kzgs.Length} kzgs and blobs.");

        Metrics.GetBlobsBundleRequests++;
        Metrics.NumberOfTransactionsInGetBlobsBundle = block.Transactions.Length;
        return ResultWrapper<BlobsBundleV1?>.Success(blobsBundle);
    }
}
