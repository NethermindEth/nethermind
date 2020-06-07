//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Crypto;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("/validator/block")]
    public class ValidatorBlockPostController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public ValidatorBlockPostController(ILogger<ValidatorBlockPostController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Publish a signed block.</summary>
        /// <remarks>
        /// <para>
        /// Instructs the beacon node to broadcast a newly signed beacon block to the beacon network, to be included in the beacon chain. The beacon node is not required to validate the signed `BeaconBlock`, and a successful response (20X) only indicates that the broadcast has been successful. The beacon node is expected to integrate the new block into its state, and therefore validate the block internally, however blocks which fail the validation are still broadcast but a different status code is returned (202).
        /// </para>
        /// </remarks>
        /// <param name="requestBody">The `BeaconBlock` object, as sent from the beacon node originally, but now with the signature field completed. Must be sent in JSON format in the body of the request.</param>
        [HttpPost]
        public async Task<IActionResult> GetAsync([FromBody] SignedBeaconBlock signedBeaconBlock,
            CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.BlockPublished(_logger, signedBeaconBlock.Message?.Slot,
                    signedBeaconBlock.Message?.Body?.RandaoReveal,
                    signedBeaconBlock.Message?.ParentRoot,
                    signedBeaconBlock.Message?.StateRoot,
                    signedBeaconBlock.Message?.Body?.Graffiti,
                    signedBeaconBlock.Signature,
                    null);

            ApiResponse apiResponse =
                await _beaconNode.PublishBlockAsync(signedBeaconBlock, cancellationToken).ConfigureAwait(false);

            return apiResponse.StatusCode switch
            {
                // "The block was validated successfully and has been broadcast. It has also been integrated into the beacon node's database."
                Core2.Api.StatusCode.Success => Ok(),
                // "The block failed validation, but was successfully broadcast anyway. It was not integrated into the beacon node's database."
                Core2.Api.StatusCode.BroadcastButFailedValidation => Accepted(),
                Core2.Api.StatusCode.InvalidRequest => Problem("Invalid request syntax.",
                    statusCode: (int) apiResponse.StatusCode),
                Core2.Api.StatusCode.CurrentlySyncing => Problem("Beacon node is currently syncing, try again later.",
                    statusCode: (int) apiResponse.StatusCode),
                _ => Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode)
            };
        }
    }
}