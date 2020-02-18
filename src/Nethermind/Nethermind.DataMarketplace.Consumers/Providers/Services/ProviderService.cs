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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Providers.Services
{
    public class ProviderService : IProviderService
    {
        private readonly IProviderRepository _providerRepository;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<PublicKey, INdmPeer> _providers =
            new ConcurrentDictionary<PublicKey, INdmPeer>();

        private readonly ConcurrentDictionary<Address, ConcurrentDictionary<PublicKey, string>>
            _providersWithCommonAddress = new ConcurrentDictionary<Address, ConcurrentDictionary<PublicKey, string>>();

        public ProviderService(IProviderRepository providerRepository, IConsumerNotifier consumerNotifier,
            ILogManager logManager)
        {
            _providerRepository = providerRepository;
            _consumerNotifier = consumerNotifier;
            _logger = logManager.GetClassLogger();
        }

        public INdmPeer? GetPeer(Address address)
        {
            if (!_providersWithCommonAddress.TryGetValue(address, out ConcurrentDictionary<PublicKey, string>? nodes) || nodes.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider nodes were not found for address: '{address}'.");
                return null;
            }

            //TODO: Select a random node and add load balancing in the future.
            KeyValuePair<PublicKey, string> nodeId = nodes.First();
            if (_providers.TryGetValue(nodeId.Key, out INdmPeer? providerPeer))
            {
                return providerPeer;
            }

            if (_logger.IsWarn) _logger.Warn($"Provider node: '{nodeId}' was not found.");
            
            return null;
        }

        public IEnumerable<INdmPeer> GetPeers() => _providers.Values;

        public Task<IReadOnlyList<ProviderInfo>> GetKnownAsync() => _providerRepository.GetProvidersAsync();

        public void Add(INdmPeer peer)
        {
            _providers.TryAdd(peer.NodeId, peer);
            AddProviderNodes(peer);
        }

        private void AddProviderNodes(INdmPeer peer)
        {
            if (peer.ProviderAddress == null)
            {
                throw new InvalidOperationException("Trying to add a provider node without a provider address known.");
            }
            
            ConcurrentDictionary<PublicKey, string> nodes = _providersWithCommonAddress.AddOrUpdate(peer.ProviderAddress,
                _ => new ConcurrentDictionary<PublicKey, string>(), (_, n) => n);
            nodes.TryAdd(peer.NodeId, string.Empty);
            if (_logger.IsInfo) _logger.Info($"Added provider peer: '{peer.NodeId}' for address: '{peer.ProviderAddress}', nodes: {nodes.Count}.");
        }

        public void Remove(PublicKey nodeId)
        {
            if (!_providers.TryRemove(nodeId, out INdmPeer? provider))
            {
                return;
            }

            if (provider.ProviderAddress == null)
            {
                throw new InvalidOperationException($"While removing a provider from {nameof(ProviderService)} found a provider without a {nameof(INdmPeer.ProviderAddress)} set.");
            }

            if (!_providersWithCommonAddress.TryGetValue(provider.ProviderAddress, out ConcurrentDictionary<PublicKey, string>? nodes))
            {
                return;
            }
            
            nodes.TryRemove(provider.NodeId, out _);
            if (nodes.Count == 0)
            {
                _providersWithCommonAddress.TryRemove(provider.ProviderAddress, out _);
            }
        }
        
        public async Task ChangeAddressAsync(INdmPeer peer, Address address)
        {
            if (peer.ProviderAddress == address)
            {
                return;
            }
            
            if (peer.ProviderAddress == null)
            {
                throw new InvalidOperationException($"While changing {nameof(INdmPeer.ProviderAddress)} to {address} found that the previous address has been null.");
            }
            
            Address previousAddress = peer.ProviderAddress;
            
            if (_logger.IsInfo) _logger.Info($"Changing provider address: '{previousAddress}' -> '{address}' for peer: '{peer.NodeId}'.");
            _providersWithCommonAddress.TryRemove(peer.ProviderAddress, out _);
            peer.ChangeProviderAddress(address);
            AddProviderNodes(peer);
            await _consumerNotifier.SendProviderAddressChangedAsync(address, previousAddress);
            if (_logger.IsInfo) _logger.Info($"Changed provider address: '{previousAddress}' -> '{address}'.");
        }
    }
}