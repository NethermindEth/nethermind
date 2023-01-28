// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("/validator/block")]
    public class ValidatorBlockController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public ValidatorBlockController(ILogger<ValidatorBlockController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Produce a new block, without signature.</summary>
        /// <remarks>
        /// <para>
        /// Requests a beacon node to produce a valid block, which can then be signed by a validator.
        /// </para>
        /// </remarks>
        /// <param name="slot">The slot for which the block should be proposed.</param>
        /// <param name="randao_reveal">The validator's randao reveal value. Hex-encoded.</param>
        [HttpGet]
        // ReSharper disable once InconsistentNaming
        // ReSharper disable once IdentifierTypo
        public async Task<IActionResult> GetAsync([FromQuery] ulong slot, [FromQuery] byte[] randao_reveal,
            CancellationToken cancellationToken)
        {
            if (_logger.IsInfo()) Log.NewBlockRequested(_logger, slot, Bytes.ToHexString(randao_reveal), null);

            Slot targetSlot = new Slot(slot);
            BlsSignature randaoReveal = new BlsSignature(randao_reveal);

            ApiResponse<BeaconBlock> apiResponse =
                await _beaconNode.NewBlockAsync(targetSlot, randaoReveal, cancellationToken).ConfigureAwait(false);

            return apiResponse.StatusCode switch
            {
                Core2.Api.StatusCode.Success => Ok(apiResponse.Content),
                Core2.Api.StatusCode.InvalidRequest => Problem("Invalid request syntax.",
                    statusCode: (int)apiResponse.StatusCode),
                Core2.Api.StatusCode.CurrentlySyncing => Problem("Beacon node is currently syncing, try again later.",
                    statusCode: (int)apiResponse.StatusCode),
                _ => Problem("Beacon node internal error.", statusCode: (int)apiResponse.StatusCode)
            };
        }
    }
}
