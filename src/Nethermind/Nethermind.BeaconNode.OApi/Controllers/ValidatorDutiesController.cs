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

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("/validator/duties")]
    public class ValidatorDutiesController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public ValidatorDutiesController(ILogger<ValidatorDutiesController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Get validator duties for the requested validators.</summary>
        /// <remarks>
        /// <para>
        /// Requests the beacon node to provide a set of _duties_, which are actions that should be performed by validators, for a particular epoch. Duties should only need to be checked once per epoch, however a chain reorganization (of > MIN_SEED_LOOKAHEAD epochs) could occur, resulting in a change of duties. For full safety, this API call should be polled at every slot to ensure that chain reorganizations are recognized, and to ensure that the beacon node is properly synchronized.
        /// </para>
        /// </remarks>
        /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        /// <param name="epoch"></param>
        /// <returns></returns>
        [HttpGet]
        // ReSharper disable once InconsistentNaming
        // ReSharper disable once IdentifierTypo
        public async Task<IActionResult> GetAsync([FromQuery] byte[][] validator_pubkeys, [FromQuery] ulong? epoch,
            CancellationToken cancellationToken)
        {
            IList<BlsPublicKey> publicKeys = validator_pubkeys.Select(x => new BlsPublicKey(x)).ToList();
            Epoch? targetEpoch = (Epoch?) epoch;
            ApiResponse<IList<ValidatorDuty>> apiResponse =
                await _beaconNode.ValidatorDutiesAsync(publicKeys, targetEpoch, cancellationToken)
                    .ConfigureAwait(false);
            switch (apiResponse.StatusCode)
            {
                case Core2.Api.StatusCode.Success:
                    return Ok(apiResponse.Content);
                case Core2.Api.StatusCode.InvalidRequest:
                    return Problem("Invalid request syntax.", statusCode: (int) apiResponse.StatusCode);
                case Core2.Api.StatusCode.CurrentlySyncing:
                    return Problem("Beacon node is currently syncing, try again later.",
                        statusCode: (int) apiResponse.StatusCode);
                case Core2.Api.StatusCode.DutiesNotAvailableForRequestedEpoch:
                    return Problem("Duties cannot be provided for the requested epoch.",
                        statusCode: (int) apiResponse.StatusCode);
            }

            return Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode);
        }
    }
}