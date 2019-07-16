/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmFaucet : INdmFaucet
    {
        private readonly ConcurrentDictionary<string, bool> _pendingRequests = new ConcurrentDictionary<string, bool>();
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IEthRequestRepository _requestRepository;
        private readonly Address _faucetAddress;
        private readonly UInt256 _maxValue;
        private readonly bool _enabled;
        private readonly ITimestamp _timestamp;
        private readonly ILogger _logger;

        public NdmFaucet(IBlockchainBridge blockchainBridge, IEthRequestRepository requestRepository,
            Address faucetAddress, UInt256 maxValue, bool enabled, ITimestamp timestamp, ILogManager logManager)
        {
            _blockchainBridge = blockchainBridge;
            _requestRepository = requestRepository;
            _faucetAddress = faucetAddress;
            _maxValue = maxValue;
            _enabled = enabled;
            _timestamp = timestamp;
            _logger = logManager.GetClassLogger();
            if (_enabled && !(_faucetAddress is null))
            {
                if (_logger.IsInfo) _logger.Info($"NDM Faucet was enabled for this host, account: {faucetAddress}, ETH request max value: {maxValue} wei");
            }
        }

        public async Task<FaucetResponse> TryRequestEthAsync(string node, Address address, UInt256 value)
        {
            if (!_enabled)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet is disabled");
                return FaucetResponse.FaucetDisabled;
            }
            
            if (_faucetAddress is null || _faucetAddress == Address.Zero)
            {
                if (_logger.IsWarn) _logger.Warn("NDM Faucet address is not set");
                return FaucetResponse.FaucetAddressNotSet;
            }

            if (string.IsNullOrWhiteSpace(node) || address is null || address == Address.Zero)
            {
                if (_logger.IsInfo) _logger.Info("Invalid NDM Faucet ETH request");
                return FaucetResponse.InvalidNodeAddress;
            }

            if (_faucetAddress == address)
            {
                if (_logger.IsInfo) _logger.Info("ETH request cannot be processed for the same address as faucet");
                return FaucetResponse.SameAddressAsFaucet;
            }
            
            if (value == 0)
            {
                if (_logger.IsInfo) _logger.Info("ETH request cannot be processed for the zero value");
                return FaucetResponse.ZeroValue;
            }
            
            if (value > _maxValue)
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {node} has too big value: {value} wei > {_maxValue} wei");
                return FaucetResponse.TooBigValue;
            }

            if (_pendingRequests.TryGetValue(node, out _))
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {node} is already being processed.");
                return FaucetResponse.RequestAlreadyProcessing;
            }

            if (_logger.IsInfo) _logger.Info($"Received ETH request from: {node}, address: {address}, value: {value} wei");
            var latestRequest = await _requestRepository.GetLatestAsync(node);
            var requestedAt = _timestamp.UtcNow;
            if (!(latestRequest is null) && latestRequest.RequestedAt.Date >= requestedAt.Date)
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {node} was already processed today at: {latestRequest.RequestedAt}");
                return FaucetResponse.RequestAlreadyProcessedToday(FaucetRequestDetails.From(latestRequest));
            }

            if (!_pendingRequests.TryAdd(node, true))
            {
                if (_logger.IsWarn) _logger.Warn($"Couldn't start processing ETH request from: {node}");
                return FaucetResponse.RequestError;
            }
            
            if (_logger.IsInfo) _logger.Info($"Processing ETH request for: {node}, address: {address}, value: {value} wei");
            try
            {
                var faucetAccount = _blockchainBridge.GetAccount(_faucetAddress);
                var transaction = new Transaction
                {
                    Value = value,
                    GasLimit = 21000,
                    GasPrice = 20.GWei(),
                    To = address,
                    SenderAddress = _faucetAddress,
                    Nonce = faucetAccount?.Nonce ?? 0
                };
                _blockchainBridge.Sign(transaction);
                var transactionHash = _blockchainBridge.SendTransaction(transaction);
                if (latestRequest is null)
                {
                    var requestId = Keccak.Compute(Rlp.Encode(Rlp.Encode(node)));
                    latestRequest = new EthRequest(requestId, node, address, value, requestedAt, transactionHash);
                    await _requestRepository.AddAsync(latestRequest);
                }
                else
                {
                    latestRequest.UpdateRequestDate(requestedAt);
                    await _requestRepository.UpdateAsync(latestRequest);
                }

                if (_logger.IsInfo) _logger.Info($"ETH request was successfully processed for: {node}, address: {address}, value: {value} wei");
                return FaucetResponse.RequestCompleted(FaucetRequestDetails.From(latestRequest));
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error(e.Message, e);
                return FaucetResponse.ProcessingRequestError;
            }
            finally
            {
                _pendingRequests.TryRemove(node, out _);
            }
        }
    }
}