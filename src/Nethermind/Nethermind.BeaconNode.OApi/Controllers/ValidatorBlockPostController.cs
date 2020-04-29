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

using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ValidatorBlockPostRequest = Nethermind.BeaconNode.OApi.Models.ValidatorBlockPostRequest;
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
        public async Task<IActionResult> GetAsync([FromBody] ValidatorBlockPostRequest requestBody,
            CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.BlockPublished(_logger, requestBody.Message?.Slot ?? 0,
                    Bytes.ToHexString(requestBody.Message?.Body?.RandaoReveal ?? new byte[0]),
                    Bytes.ToHexString(requestBody.Message?.ParentRoot ?? new byte[0]), 
                    Bytes.ToHexString(requestBody.Message?.StateRoot ?? new byte[0]),
                    Bytes.ToHexString(requestBody.Message?.Body?.Graffiti ?? new byte[0]),
                    Bytes.ToHexString(requestBody.Signature ?? new byte[0]), 
                    null);
              
            // TODO: Move to MapXxxx methods
            SignedBeaconBlock signedBeaconBlock = new SignedBeaconBlock(
                new BeaconBlock(
                        new Slot(requestBody.Message!.Slot!.Value),
                        new Root(requestBody.Message.ParentRoot),
                        new Root(requestBody.Message.StateRoot),
                        new BeaconBlockBody(
                            new BlsSignature(requestBody.Message.Body!.RandaoReveal), 
                            new Eth1Data(
                                new Root(requestBody.Message.Body.Eth1Data!.DepositRoot),
                                requestBody.Message.Body.Eth1Data.DepositCount!.Value,
                                new Bytes32(requestBody.Message.Body.Eth1Data.BlockHash)
                                ), 
                            new Bytes32(requestBody.Message.Body.Graffiti), 
                            new ProposerSlashing[0],
                            new AttesterSlashing[0],
                            new Attestation[0],
                            requestBody.Message.Body.Deposits.Select(x => new Deposit(
                                    x.Proof.Select(y => new Bytes32(y)),
                                    new DepositData(
                                        new BlsPublicKey(x.Data!.PublicKey), 
                                        new Bytes32(x.Data.WithdrawalCredentials), 
                                        new Gwei(x.Data.Amount!.Value), 
                                        new BlsSignature(x.Data.Signature)
                                        )
                                )).ToList(),
                            new SignedVoluntaryExit[0]
                            )
                    ), 
                new BlsSignature(requestBody.Signature)
                );              
              
            ApiResponse apiResponse =
                await _beaconNode.PublishBlockAsync(signedBeaconBlock, cancellationToken).ConfigureAwait(false);

            switch (apiResponse.StatusCode)
            {
                case Core2.Api.StatusCode.Success:
                    // "The block was validated successfully and has been broadcast. It has also been integrated into the beacon node's database."
                    return Ok();
                case Core2.Api.StatusCode.BroadcastButFailedValidation:
                    // "The block failed validation, but was successfully broadcast anyway. It was not integrated into the beacon node's database."
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