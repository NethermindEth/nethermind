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