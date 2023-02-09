// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("node/genesis_time")]
    public class NodeGenesisTimeController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public NodeGenesisTimeController(ILogger<NodeGenesisTimeController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        /// <remarks>
        /// <para>
        /// Requests the genesis_time parameter from the beacon node, which should be consistent across all beacon nodes that follow the same beacon chain.
        /// </para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.NodeGenesisTimeRequested(_logger, null);

            ApiResponse<ulong> apiResponse =
                await _beaconNode.GetGenesisTimeAsync(cancellationToken).ConfigureAwait(false);
            if (apiResponse.StatusCode == Core2.Api.StatusCode.Success)
            {
                return Ok(apiResponse.Content);
            }

            return Problem("Beacon node internal error.", statusCode: (int)apiResponse.StatusCode);
        }
    }
}
