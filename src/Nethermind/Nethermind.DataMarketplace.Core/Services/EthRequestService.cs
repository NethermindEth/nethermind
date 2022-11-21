// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class EthRequestService : IEthRequestService
    {
        private INdmPeer? _faucetPeer;
        private readonly ILogger _logger;

        public string? FaucetHost { get; }

        public EthRequestService(string? faucetHost, ILogManager logManager)
        {
            FaucetHost = faucetHost;
            _logger = logManager.GetClassLogger();
        }

        public void UpdateFaucet(INdmPeer peer)
        {
            _faucetPeer = peer;
            if (_logger.IsInfo) _logger.Info($"Updated NDM faucet peer: {peer.NodeId}");
        }

        public async Task<FaucetResponse> TryRequestEthAsync(Address address, UInt256 value)
        {
            if (_faucetPeer is null)
            {
                if (_logger.IsWarn) _logger.Warn("ETH request to NDM faucet will not be send (no peer).");
                return FaucetResponse.FaucetNotSet;
            }

            if (address is null || address == Address.Zero || value == 0)
            {
                return FaucetResponse.InvalidNodeAddress;
            }

            if (_logger.IsInfo) _logger.Info($"Sending ETH request to NDM faucet, address: {address}, value {value} wei");
            var response = await _faucetPeer.SendRequestEthAsync(address, value);
            if (_logger.IsInfo) _logger.Info($"Received response to ETH request from NDM faucet (address: {address}, value {value} wei) -> status: {response.Status}.");

            return response;
        }
    }
}
