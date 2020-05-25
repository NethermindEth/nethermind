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
using Nethermind.Core2.Containers;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("/validator/attestation")]
    public class ValidatorAttestationPostController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public ValidatorAttestationPostController(ILogger<ValidatorAttestationPostController> logger,
            IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Publish a signed attestation.</summary>
        /// <remarks>
        /// <para>
        /// Instructs the beacon node to broadcast a newly signed Attestation object to the intended shard subnet. The beacon node is not required to validate the signed Attestation, and a successful response (20X) only indicates that the broadcast has been successful. The beacon node is expected to integrate the new attestation into its state, and therefore validate the attestation internally, however attestations which fail the validation are still broadcast but a different status code is returned (202)
        /// </para>
        /// </remarks>
        /// <param name="requestBody">An `Attestation` structure, as originally provided by the beacon node, but now with the signature field completed. Must be sent in JSON format in the body of the request.</param>
        [HttpPost]
        public async Task<IActionResult> GetAsync([FromBody] Attestation signedAttestation,
            CancellationToken cancellationToken)
        {
            if (_logger.IsDebug())
            {
                string? aggregationBits = null;
                if (signedAttestation.AggregationBits != null)
                {
                    aggregationBits = string.Create(signedAttestation.AggregationBits.Length,
                        signedAttestation.AggregationBits,
                        ((span, bitArray) =>
                        {
                            for (int index = 0; index < bitArray.Length; index++)
                            {
                                span[index] = bitArray[index] ? '1' : '0';
                            }
                        }));
                }

                LogDebug.AttestationPublished(_logger, signedAttestation.Data?.Slot,
                    signedAttestation.Data?.Index,
                    aggregationBits,
                    signedAttestation.Signature,
                    null);
            }

            ApiResponse apiResponse =
                await _beaconNode.PublishAttestationAsync(signedAttestation, cancellationToken).ConfigureAwait(false);

            switch (apiResponse.StatusCode)
            {
                case Core2.Api.StatusCode.Success:
                    // "The attestation was validated successfully and has been broadcast. It has also been integrated into the beacon node's database."
                    return Ok();
                case Core2.Api.StatusCode.BroadcastButFailedValidation:
                    // "The attestation failed validation, but was successfully broadcast anyway. It was not integrated into the beacon node's database."
                    return Accepted();
                case Core2.Api.StatusCode.InvalidRequest:
                    return Problem("Invalid request syntax.", statusCode: (int) apiResponse.StatusCode);
                case Core2.Api.StatusCode.CurrentlySyncing:
                    return Problem("Beacon node is currently syncing, try again later.",
                        statusCode: (int) apiResponse.StatusCode);
            }

            return Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode);
        }
    }
}