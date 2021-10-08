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
                    statusCode: (int) apiResponse.StatusCode),
                Core2.Api.StatusCode.CurrentlySyncing => Problem("Beacon node is currently syncing, try again later.",
                    statusCode: (int) apiResponse.StatusCode),
                _ => Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode)
            };
        }
    }
}