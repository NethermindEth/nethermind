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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmFaucet : INdmFaucet
    {
        private readonly object _locker = new object();
        private DateTime _today;
        private UInt256 _todayRequestsTotalValueWei = 0;
        private readonly ConcurrentDictionary<string, bool> _pendingRequests = new ConcurrentDictionary<string, bool>();
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IEthRequestRepository _requestRepository;
        private readonly Address? _faucetAddress;
        private readonly UInt256 _maxValue;
        private readonly UInt256 _dailyRequestsTotalValueWei;
        private readonly bool _enabled;
        private readonly ITimestamper _timestamper;
        private readonly IWallet _wallet;
        private readonly ILogger _logger;
        private bool _initialized;

        public NdmFaucet(
            INdmBlockchainBridge? blockchainBridge,
            IEthRequestRepository? requestRepository,
            Address? faucetAddress,
            UInt256 maxValue,
            UInt256 dailyRequestsTotalValueEth,
            bool enabled,
            ITimestamper? timestamper,
            IWallet? wallet,
            ILogManager? logManager)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _requestRepository = requestRepository ?? throw new ArgumentNullException(nameof(requestRepository));
            _faucetAddress = faucetAddress;
            _maxValue = maxValue;
            _dailyRequestsTotalValueWei = dailyRequestsTotalValueEth * 1_000_000_000_000_000_000;
            _enabled = enabled;
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _today = _timestamper.UtcNow;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            if (!_enabled || _faucetAddress is null)
            {
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"NDM Faucet was enabled for this host, account: {faucetAddress}, request max value: {maxValue} wei");
            _requestRepository.SumDailyRequestsTotalValueAsync(_today).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Error during NDM faucet today's ({_today.Date:d}) total value initialization.", t.Exception);
                    return;
                }

                _todayRequestsTotalValueWei = t.Result;
                _initialized = true;
                if (_logger.IsInfo) _logger.Info($"Initialized NDM faucet today's ({_today.Date:d}) total value: {_todayRequestsTotalValueWei} wei");
            });
        }

        public bool IsInitialized => _initialized;

        public async Task<FaucetResponse> TryRequestEthAsync(string node, Address? address, UInt256 value)
        {
            if (!_enabled)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet is disabled.");
                return FaucetResponse.FaucetDisabled;
            }
            
            if (!_initialized)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet is not initialized.");
                return FaucetResponse.FaucetDisabled;
            }
            
            if (_today.Date != _timestamper.UtcNow.Date)
            {
                lock (_locker)
                {
                    _today = _timestamper.UtcNow;
                    _todayRequestsTotalValueWei = 0;
                }
                
                if (_logger.IsInfo) _logger.Info($"NDM Faucet has updated its today's date ({_today.Date:d}) and reset the total requests value.");
            }
            
            if (_faucetAddress is null || _faucetAddress == Address.Zero)
            {
                if (_logger.IsWarn) _logger.Warn("NDM Faucet address is not set.");
                return FaucetResponse.FaucetAddressNotSet;
            }

            if (string.IsNullOrWhiteSpace(node) || address is null || address == Address.Zero)
            {
                if (_logger.IsInfo) _logger.Info("Invalid NDM Faucet request.");
                return FaucetResponse.InvalidNodeAddress;
            }

            if (_faucetAddress == address)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet request cannot be processed for the same address as NDM Faucet.");
                return FaucetResponse.SameAddressAsFaucet;
            }
            
            if (value == 0)
            {
                if (_logger.IsInfo) _logger.Info("NDM Faucet request cannot be processed for the zero value.");
                return FaucetResponse.ZeroValue;
            }
            
            if (value > _maxValue)
            {
                if (_logger.IsInfo) _logger.Info($"NDM Faucet request from: {node} has too big value: {value} wei > {_maxValue} wei.");
                return FaucetResponse.TooBigValue;
            }

            if (_logger.IsInfo) _logger.Info($"Received NDM Faucet request from: {node}, address: {address}, value: {value} wei.");
            if (_pendingRequests.TryGetValue(node, out _))
            {
                if (_logger.IsInfo) _logger.Info($"NDM Faucet request from: {node} is already being processed.");
                return FaucetResponse.RequestAlreadyProcessing;
            }
            
            var latestRequest = await _requestRepository.GetLatestAsync(node);
            var requestedAt = _timestamper.UtcNow;
            if (!(latestRequest is null) && latestRequest.RequestedAt.Date >= requestedAt.Date)
            {
                if (_logger.IsInfo) _logger.Info($"NDM Faucet request from: {node} was already processed today at: {latestRequest.RequestedAt}.");
                return FaucetResponse.RequestAlreadyProcessedToday(FaucetRequestDetails.From(latestRequest));
            }

            if (!_pendingRequests.TryAdd(node, true))
            {
                if (_logger.IsWarn) _logger.Warn($"Couldn't start processing NDM Faucet request from: {node}.");
                return FaucetResponse.RequestError;
            }
            
            lock (_locker)
            {
                _todayRequestsTotalValueWei += value;
                if (_logger.IsInfo) _logger.Info($"Increased NDM Faucet total value of today's ({_today.Date:d}) requests to {_todayRequestsTotalValueWei} wei.");
            }
            
            if (_todayRequestsTotalValueWei > _dailyRequestsTotalValueWei)
            {
                if (_logger.IsInfo) _logger.Info($"Daily ({_today.Date:d}) requests value for NDM Faucet was reached ({_dailyRequestsTotalValueWei} wei).");
                return FaucetResponse.DailyRequestsTotalValueReached;
            }

            if (_logger.IsInfo) _logger.Info($"NDM Faucet is processing request for: {node}, address: {address}, value: {value} wei.");
            try
            {
                var nonce =  await _blockchainBridge.GetNonceAsync(_faucetAddress);
                var transaction = new Transaction
                {
                    Value = value,
                    GasLimit = Transaction.BaseTxGasCost,
                    GasPrice = 20.GWei(),
                    To = address,
                    SenderAddress = _faucetAddress,
                    Nonce = nonce
                };
                _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());
                Keccak? transactionHash = await _blockchainBridge.SendOwnTransactionAsync(transaction);
                if (transactionHash == null)
                {
                    return FaucetResponse.ProcessingRequestError;
                }
                
                if (latestRequest is null)
                {
                    Keccak requestId = Keccak.Compute(Rlp.Encode(Rlp.Encode(node)).Bytes);
                    latestRequest = new EthRequest(requestId, node, address, value, requestedAt, transactionHash);
                    await _requestRepository.AddAsync(latestRequest);
                }
                else
                {
                    latestRequest.UpdateRequestDetails(address, value, requestedAt, transactionHash);
                    await _requestRepository.UpdateAsync(latestRequest);
                }

                if (_logger.IsInfo) _logger.Info($"NDM Faucet has successfully processed request for: {node}, address: {address}, value: {value} wei.");
                return FaucetResponse.RequestCompleted(FaucetRequestDetails.From(latestRequest));
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.Message, ex);
                lock (_locker)
                {
                    _todayRequestsTotalValueWei -= value;
                    if (_logger.IsInfo) _logger.Info($"Decreased NDM Faucet total value of today's ({_today.Date:d}) requests to {_todayRequestsTotalValueWei} wei.");
                }
                
                return FaucetResponse.ProcessingRequestError;
            }
            finally
            {
                _pendingRequests.TryRemove(node, out _);
            }
        }
    }
}
