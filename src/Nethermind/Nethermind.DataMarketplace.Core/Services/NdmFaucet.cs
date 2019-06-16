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

        public async Task<bool> TryRequestEthAsync(string host, Address address, UInt256 value)
        {
            if (!_enabled)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet is disabled");
                return false;
            }
            
            if (_faucetAddress is null || _faucetAddress == Address.Zero)
            {
                if (_logger.IsWarn) _logger.Warn("NDM Faucet address is not set");
                return false;
            }

            if (string.IsNullOrWhiteSpace(host) || address is null || address == Address.Zero ||
                _faucetAddress == address || value == 0)
            {
                if (_logger.IsInfo) _logger.Info("Invalid NDM Faucet ETH request");
                return false;
            }
            
            if (value > _maxValue)
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {host} has too big value: {value} wei > {_maxValue} wei");
                return false;
            }

            if (_pendingRequests.TryGetValue(host, out _))
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {host} is already being processed.");
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Received ETH request from: {host}, address: {address}, value: {value} wei");
            var latestRequest = await _requestRepository.GetLatestAsync(host);
            var requestedAt = _timestamp.UtcNow;
            if (!(latestRequest is null) && latestRequest.RequestedAt.Date >= requestedAt.Date)
            {
                if (_logger.IsInfo) _logger.Info($"ETH request from: {host} was already processed today at: {latestRequest.RequestedAt}");
                return false;
            }

            if (!_pendingRequests.TryAdd(host, true))
            {
                if (_logger.IsWarn) _logger.Warn($"Couldn't start processing ETH request from: {host}");
                return false;
            }
            
            if (_logger.IsInfo) _logger.Info($"Processing ETH request for: {host}, address: {address}, value: {value} wei");
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
                    var requestId = Keccak.Compute(Rlp.Encode(Rlp.Encode(host)));
                    await _requestRepository.AddAsync(new EthRequest(requestId, host, address, value, requestedAt,
                        transactionHash));
                }
                else
                {
                    latestRequest.UpdateRequestDate(requestedAt);
                    await _requestRepository.UpdateAsync(latestRequest);
                }

                if (_logger.IsInfo) _logger.Info($"ETH request was successfully processed for: {host}, address: {address}, value: {value} wei");
                return true;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error(e.Message, e);
                return false;
            }
            finally
            {
                _pendingRequests.TryRemove(host, out _);
            }
        }
    }
}