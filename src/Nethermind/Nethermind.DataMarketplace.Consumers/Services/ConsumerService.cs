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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Consumers.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Wallet;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;
using SessionState = Nethermind.DataMarketplace.Core.Domain.SessionState;
using Timer = System.Timers.Timer;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public class ConsumerService : IConsumerService
    {
        private readonly ConcurrentDictionary<Keccak, ConsumerSession> _sessions =
            new ConcurrentDictionary<Keccak, ConsumerSession>();

        private readonly ConcurrentDictionary<Keccak, DataHeader> _discoveredDataHeaders =
            new ConcurrentDictionary<Keccak, DataHeader>();

        private readonly ConcurrentDictionary<PublicKey, INdmPeer> _providers =
            new ConcurrentDictionary<PublicKey, INdmPeer>();

        private readonly ConcurrentDictionary<Address, ConcurrentDictionary<PublicKey, string>>
            _providersWithCommonAddress = new ConcurrentDictionary<Address, ConcurrentDictionary<PublicKey, string>>();

        private readonly ConcurrentDictionary<Keccak, DepositDetails> _deposits =
            new ConcurrentDictionary<Keccak, DepositDetails>();

        private readonly AbiSignature _depositAbiSig = new AbiSignature("deposit",
            new AbiBytes(32),
            new AbiUInt(32),
            new AbiUInt(96),
            new AbiUInt(32),
            new AbiBytes(16),
            AbiType.Address,
            AbiType.Address);

        private readonly AbiSignature _dataDeliveryReceiptAbiSig = new AbiSignature("dataDeliveryReceipt",
            new AbiBytes(32),
            new AbiFixedLengthArray(new AbiUInt(32), 2));

        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IConsumerDepositApprovalRepository _depositApprovalRepository;
        private readonly IProviderRepository _providerRepository;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly IWallet _wallet;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly IDepositService _depositService;
        private readonly IReceiptRequestValidator _receiptRequestValidator;
        private readonly IRefundService _refundService;
        private readonly IBlockchainBridge _blockchainBridge;
        private Address _consumerAddress;
        private readonly PublicKey _nodePublicKey;
        private readonly ITimestamp _timestamp;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly uint _blockConfirmations;
        private readonly ILogger _logger;
        private readonly Timer _timer;
        private readonly IEcdsa _ecdsa = new Ecdsa();
        private bool _accountLocked;

        public ConsumerService(IConfigManager configManager, string configId,
            IDepositDetailsRepository depositRepository, IConsumerDepositApprovalRepository depositApprovalRepository,
            IProviderRepository providerRepository, IReceiptRepository receiptRepository,
            IConsumerSessionRepository sessionRepository, IWallet wallet, IAbiEncoder abiEncoder,
            ICryptoRandom cryptoRandom, IDepositService depositService,
            IReceiptRequestValidator receiptRequestValidator, IRefundService refundService,
            IBlockchainBridge blockchainBridge, Address consumerAddress, PublicKey nodePublicKey,
            ITimestamp timestamp, IConsumerNotifier consumerNotifier, uint blockConfirmations, ILogManager logManager)
        {
            _configManager = configManager;
            _configId = configId;
            _depositRepository = depositRepository;
            _depositApprovalRepository = depositApprovalRepository;
            _providerRepository = providerRepository;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _wallet = wallet;
            _abiEncoder = abiEncoder;
            _cryptoRandom = cryptoRandom;
            _depositService = depositService;
            _receiptRequestValidator = receiptRequestValidator;
            _refundService = refundService;
            _blockchainBridge = blockchainBridge;
            _consumerAddress = consumerAddress ?? Address.Zero;
            _nodePublicKey = nodePublicKey;
            _timestamp = timestamp;
            _consumerNotifier = consumerNotifier;
            _blockConfirmations = blockConfirmations;
            _logger = logManager.GetClassLogger();
            _timer = new Timer {Interval = 5000};
            _timer.Elapsed += OnTimeElapsed;
            _timer.Start();
            _wallet.AccountLocked += OnAccountLocked;
            _wallet.AccountUnlocked += OnAccountUnlocked;
            _accountLocked = !_wallet.IsUnlocked(_consumerAddress);
        }

        private void OnAccountUnlocked(object sender, AccountUnlockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }

            _accountLocked = false;
            if (_logger.IsInfo) _logger.Info($"Unlocked consumer account: '{e.Address}', data streams can be enabled.");
        }

        private void OnAccountLocked(object sender, AccountLockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }
            
            _accountLocked = true;
            if (_logger.IsInfo) _logger.Info($"Locked consumer account: '{e.Address}', all of the existing data streams will be disabled.");

            var disableStreamTasks = _sessions.Values.Select(s => DisableDataStreamAsync(s.DepositId));
            Task.WhenAll(disableStreamTasks).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Disabling data stream failed.", t.Exception);
                }
            });
        }

        private void OnTimeElapsed(object sender, ElapsedEventArgs e)
            => _depositRepository.BrowseAsync(new GetDeposits
                {
                    OnlyUnverified = true,
                    Results = int.MaxValue
                })
                .ContinueWith(async t =>
                {
                    for (var i = 0; i < t.Result.Items.Count; i++)
                    {
                        var depositDetails = t.Result.Items[i];
                        await TryVerifyDepositAsync(depositDetails).ContinueWith(verifyDepositTask =>
                        {
                            if (verifyDepositTask.IsFaulted && _logger.IsError)
                            {
                                _logger.Error($"Verifying deposit: '{depositDetails.Id}' failed.",
                                    verifyDepositTask.Exception);
                            }
                        });
                        await TryClaimRefundAsync(depositDetails).ContinueWith(claimRefundTask =>
                            {
                                if (claimRefundTask.IsFaulted && _logger.IsError)
                                {
                                    _logger.Error($"Claiming refund for deposit: '{depositDetails.Id}' failed.",
                                        claimRefundTask.Exception);
                                }
                            }
                        );
                    }
                });

        private async Task TryVerifyDepositAsync(DepositDetails depositDetails)
        {
            var verificationTimestamp = _depositService.VerifyDeposit(depositDetails.Id);
            if (verificationTimestamp == 0)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{depositDetails.Id}' didn't return verification timestamp from contract call yet.'");
                return;
            }

            var transactionHash = depositDetails.TransactionHash;
            var (receipt, transaction) = _blockchainBridge.GetTransaction(depositDetails.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositDetails.Id}' to be verified.");
                return;
            }
            
            var confirmations = _blockchainBridge.Head.Number - receipt.BlockNumber;
            if (_logger.IsInfo) _logger.Info($"Deposit: '{depositDetails.Id}' has {confirmations} confirmations (required at least {_blockConfirmations}) for transaction hash: '{transactionHash}' to be verified.");
            var confirmed = confirmations >= _blockConfirmations;
            if (confirmed)
            {
                depositDetails.Verify(verificationTimestamp);
                await _depositRepository.UpdateAsync(depositDetails);
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{depositDetails.Deposit.Id}' has been verified, timestamp: {verificationTimestamp}.");
            }

            await _consumerNotifier.SendDepositConfirmationsStatusAsync(depositDetails.Id, (int) confirmations,
                (int) _blockConfirmations);
        }

        private async Task TryClaimRefundAsync(DepositDetails depositDetails)
        {
            var now = (ulong) _blockchainBridge.Head.Timestamp;
            if (depositDetails.CanClaimEarlyRefund(now))
            {
                await ClaimEarlyRefundAsync(depositDetails);
                return;
            }

            if (depositDetails.CanClaimRefund(now, depositDetails.Deposit.Units))
            {
                await ClaimRefundAsync(depositDetails);
            }
        }

        public event EventHandler<AddressChangedEventArgs> AddressChanged;
        public Address GetAddress() => _consumerAddress;

        public async Task ChangeAddressAsync(Address address)
        {
            if (_consumerAddress == address)
            {
                return;
            }

            var oldAddress = _consumerAddress;
            if (_logger.IsInfo) _logger.Info($"Changing consumer address: '{oldAddress}' -> '{address}'...");
            _consumerAddress = address;
            _accountLocked = !_wallet.IsUnlocked(_consumerAddress);
            AddressChanged?.Invoke(this, new AddressChangedEventArgs(oldAddress, _consumerAddress));
            _depositService.ChangeConsumerAddress(_consumerAddress);
            var config = await _configManager.GetAsync(_configId);
            config.ConsumerAddress = _consumerAddress.ToString();
            await _configManager.UpdateAsync(config);
            foreach (var (_, provider) in _providers)
            {
                provider.ChangeHostConsumerAddress(_consumerAddress);
                provider.SendConsumerAddressChanged(_consumerAddress);
                await FinishSessionsAsync(provider, false);
            }

            if (_logger.IsInfo) _logger.Info($"Changed consumer address: '{_consumerAddress}' -> '{address}'.");
        }

        public Task ChangeProviderAddressAsync(INdmPeer peer, Address address)
        {
            if (peer.ProviderAddress == address)
            {
                return Task.CompletedTask;
            }
            
            if (_logger.IsInfo) _logger.Info($"Changing provider address: '{peer.ProviderAddress}' -> '{address}' for peer: '{peer.NodeId}'.");
            _providersWithCommonAddress.TryRemove(peer.ProviderAddress, out _);
            peer.ChangeProviderAddress(address);
            AddProviderNodes(peer);
            
            return Task.CompletedTask;
        }

        public void AddProviderPeer(INdmPeer peer)
        {
            _providers.TryAdd(peer.NodeId, peer);
            AddProviderNodes(peer);
        }

        private void AddProviderNodes(INdmPeer peer)
        {
            var nodes = _providersWithCommonAddress.AddOrUpdate(peer.ProviderAddress,
                _ => new ConcurrentDictionary<PublicKey, string>(), (_, n) => n);
            nodes.TryAdd(peer.NodeId, string.Empty);
            if (_logger.IsInfo) _logger.Info($"Added provider peer: '{peer.NodeId}' for address: '{peer.ProviderAddress}', nodes: {nodes.Count}.");
        }

        public void AddDiscoveredDataHeader(DataHeader dataHeader, INdmPeer peer)
        {
            _discoveredDataHeaders.TryAdd(dataHeader.Id, dataHeader);
        }

        public void AddDiscoveredDataHeaders(DataHeader[] dataHeaders, INdmPeer peer)
        {
            for (var i = 0; i < dataHeaders.Length; i++)
            {
                var dataHeader = dataHeaders[i];
                _discoveredDataHeaders.TryAdd(dataHeader.Id, dataHeader);
            }
        }

        public void ChangeDataHeaderState(Keccak dataHeaderId, DataHeaderState state)
        {
            if (!_discoveredDataHeaders.TryGetValue(dataHeaderId, out var dataHeader))
            {
                return;
            }
            
            dataHeader.SetState(state);
            if (_logger.IsInfo) _logger.Info($"Changed discovered data header: '{dataHeaderId}' state to: '{state}'.");
        }

        public void RemoveDiscoveredDataHeader(Keccak dataHeaderId)
        {
            _discoveredDataHeaders.TryRemove(dataHeaderId, out _);
        }

        public async Task StartSessionAsync(Session session, INdmPeer provider)
        {
            if (!_providers.TryGetValue(provider.NodeId, out var providerPeer))
            {
                if (_logger.IsInfo) _logger.Info($"Cannot start the session: '{session.Id}', provider: '{provider.NodeId}' was not found.");

                return;
            }

            var depositDetails = await GetDepositAsync(session.DepositId);
            if (depositDetails is null)
            {
                if (_logger.IsInfo) _logger.Info($"Cannot start the session: '{session.Id}', deposit: '{session.DepositId}' was not found.");

                return;
            }

            var dataHeaderId = depositDetails.DataHeader.Id;
            if (!_discoveredDataHeaders.TryGetValue(dataHeaderId, out var dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Available data header: '{dataHeaderId}' was not found.");

                return;
            }

            if (!IsDataHeaderAvailable(dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Data header: '{dataHeaderId}' is unavailable, state: {dataHeader.State}.");

                return;
            }

            if (!providerPeer.ProviderAddress.Equals(depositDetails.DataHeader.Provider.Address))
            {
                if (_logger.IsInfo) _logger.Info($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', provider address (peer): '{providerPeer.ProviderAddress}' doesn't equal the address from data header: '{depositDetails.DataHeader.Provider.Address}'.");

                return;
            }

            if (!_discoveredDataHeaders.TryGetValue(depositDetails.DataHeader.Id, out _))
            {
                if (_logger.IsInfo) _logger.Info($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', discovered data header: '{depositDetails.DataHeader.Id}' was not found.");

                return;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = session.DepositId,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            if (_logger.IsInfo) _logger.Info($"Starting the session: '{session.Id}' for deposit: '{session.DepositId}'. Settings consumed units - provider: {session.StartUnitsFromProvider}, consumer: {consumedUnits}.");
            var consumerSession = ConsumerSession.From(session);
            consumerSession.Start(session.StartTimestamp);
            var previousSession = await _sessionRepository.GetPreviousAsync(consumerSession);
            var upfrontUnits = (uint) (depositDetails.DataHeader.Rules.UpfrontPayment?.Value ?? 0);
            if (upfrontUnits > 0 && previousSession is null)
            {
                consumerSession.AddUnpaidUnits(upfrontUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {upfrontUnits} for session: '{session.Id}' based on upfront payment.");
            }

            var unpaidUnits = previousSession?.UnpaidUnits ?? 0;
            if (unpaidUnits > 0 && !(previousSession is null))
            {
                consumerSession.AddUnpaidUnits(unpaidUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {unpaidUnits} for session: '{session.Id}' from previous session: '{previousSession.Id}'.");
            }
            
            if (depositDetails.DataHeader.UnitType == DataHeaderUnitType.Time)
            {
                var unpaidTimeUnits = (uint) consumerSession.StartTimestamp - depositDetails.VerificationTimestamp;
                consumerSession.AddUnpaidUnits(unpaidTimeUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: '{unpaidTimeUnits}' for deposit: '{session.DepositId}' based on time.");
            }

            await _sessionRepository.AddAsync(consumerSession);
            SetActiveSession(consumerSession);
            if (_logger.IsInfo) _logger.Info($"Started a session with id: '{session.Id}' for deposit: '{session.DepositId}', address: '{_consumerAddress}'.");
        }

        public async Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider)
        {
            var (deposit, session) = TryGetDepositAndSession(depositId);
            if (session is null)
            {
                return;
            }
            
            session.SetConsumedUnitsFromProvider(consumedUnitsFromProvider);
            switch (deposit.DataHeader.UnitType)
            {
                case DataHeaderUnitType.Time:
                    var now = (uint) _timestamp.EpochSeconds;
                    var currentlyConsumedUnits = now - deposit.VerificationTimestamp;
                    var currentlyUnpaidUnits = currentlyConsumedUnits > session.PaidUnits
                        ? currentlyConsumedUnits - session.PaidUnits
                        : 0;
                    session.SetConsumedUnits((uint)(now - session.StartTimestamp));
                    session.SetUnpaidUnits(currentlyUnpaidUnits);
                    break;
                case DataHeaderUnitType.Unit:
                    session.IncrementConsumedUnits();
                    session.IncrementUnpaidUnits();
                    break;
            }
            
            var consumedUnits = session.ConsumedUnits;
            var unpaidUnits = session.UnpaidUnits;
            if (_logger.IsTrace) _logger.Trace($"Setting units, consumed: [provider: {consumedUnitsFromProvider}, consumer: {consumedUnits}], unpaid: {unpaidUnits}, paid: {session.PaidUnits}, for deposit: '{depositId}', session: '{session.Id}'.");
            if (consumedUnitsFromProvider > consumedUnits)
            {
                var unitsDifference = consumedUnitsFromProvider - consumedUnits;
                if (_logger.IsTrace) _logger.Trace($"Provider has counted more consumed units ({unitsDifference}) for deposit: '{depositId}', session: '{session.Id}'");
            }
            else if (consumedUnitsFromProvider < consumedUnits)
            {
                var unitsDifference = consumedUnits - consumedUnitsFromProvider;
                if (_logger.IsTrace) _logger.Trace($"Provider has counted less consumed units ({unitsDifference}) for deposit: '{depositId}', session: '{session.Id}'.");
                
                //Adjust units?
//                session.SubtractUnpaidUnits(unpaidUnits);
//                session.SubtractUnpaidUnits(unitsDifference);
            }
            
            await _sessionRepository.UpdateAsync(session);
        }

        public async Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Setting data availability: '{dataAvailability}', for deposit: '{depositId}', session: {session.Id}.");
            session.SetDataAvailability(dataAvailability);
            await _sessionRepository.UpdateAsync(session);
        }

        public async Task<Keccak> MakeDepositAsync(Keccak headerId, uint units, UInt256 value)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't make a deposit.");
                
                return null;
            }
            
            if (!_discoveredDataHeaders.TryGetValue(headerId, out var dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Available data header: '{headerId}' was not found.");

                return null;
            }

            if (!IsDataHeaderAvailable(dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Data header: '{headerId}' is unavailable, state: {dataHeader.State}.");

                return null;
            }

            if (!(await VerifyKycAsync(dataHeader)))
            {
                return null;
            }

            var providerAddress = dataHeader.Provider.Address;
            if (!_providersWithCommonAddress.TryGetValue(providerAddress, out var nodes) || nodes.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider nodes were not found for address: '{providerAddress}'.");

                return null;
            }

            if (dataHeader.MinUnits > units || dataHeader.MaxUnits < units)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid data request units: '{units}', min: '{dataHeader.MinUnits}', max: '{dataHeader.MaxUnits}'.");

                return null;
            }

            var unitsValue = units * dataHeader.UnitPrice;
            if (units * dataHeader.UnitPrice != value)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid data request value: '{value}', while it should be: '{unitsValue}'.");

                return null;
            }

            var now = (uint) _timestamp.EpochSeconds;
            var expiryTime = now + (uint) dataHeader.Rules.Expiry.Value;
            expiryTime += dataHeader.UnitType == DataHeaderUnitType.Unit ? 0 : units;
            var pepper = _cryptoRandom.GenerateRandomBytes(16);
            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _depositAbiSig,
                headerId.Bytes, units, value, expiryTime, pepper, dataHeader.Provider.Address, _consumerAddress);
            var depositId = Keccak.Compute(abiHash);
            var deposit = new Deposit(depositId, units, expiryTime, value);
            var transactionHash = _depositService.MakeDeposit(_consumerAddress, deposit);
            var depositDetails = new DepositDetails(deposit, dataHeader, pepper, now, transactionHash);
            await _depositRepository.AddAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Making a deposit with id: '{depositId}' for data header: '{headerId}', address: '{_consumerAddress}'.");

            return depositId;
        }

        public IReadOnlyList<Address> GetConnectedProviders()
            => _providers.Values.Select(p => p.ProviderAddress).ToArray();

        public IReadOnlyList<ConsumerSession> GetActiveSessions() => _sessions.Values.ToArray();
        
        public IReadOnlyList<DataHeader> GetDiscoveredDataHeaders()
            => _discoveredDataHeaders.Values.Where(h => h.State == DataHeaderState.Published ||
                                                        h.State == DataHeaderState.UnderMaintenance).ToArray();

        public Task<IReadOnlyList<ProviderInfo>> GetKnownProvidersAsync()
            => _providerRepository.GetProvidersAsync();

        public Task<IReadOnlyList<DataHeaderInfo>> GetKnownDataHeadersAsync()
            => _providerRepository.GetDataHeadersAsync();
        
        public async Task<PagedResult<DepositDetails>> GetDepositsAsync(GetDeposits query)
        {
            var deposits = await _depositRepository.BrowseAsync(query);
            foreach (var deposit in deposits.Items)
            {
                var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
                {
                    DepositId = deposit.Id,
                    Results = int.MaxValue
                });
                var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
                deposit.SetConsumedUnits(consumedUnits);
            }

            return deposits;
        }

        public async Task<DepositDetails> GetDepositAsync(Keccak depositId)
        {
            var deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                return null;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = deposit.Id,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            deposit.SetConsumedUnits(consumedUnits);

            return deposit;
        }

        public async Task<Keccak> SendDataRequestAsync(Keccak depositId)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't send a data request.");
                
                return null;
            }
            
            if (!_deposits.TryGetValue(depositId, out var depositDetails))
            {
                depositDetails = await GetDepositAsync(depositId);
                if (depositDetails is null)
                {
                    if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was not found.'");
                    return null;
                }

                _deposits.TryAdd(depositId, depositDetails);
            }

            if (!(await VerifyKycAsync(depositDetails.DataHeader)))
            {
                return null;
            }

            if (!depositDetails.Verified)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' is not verified.'");

                return null;
            }

            var providerPeer = GetProviderPeer(depositDetails.DataHeader.Provider.Address);
            if (providerPeer is null)
            {
                return null;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = depositId,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            if (_logger.IsInfo) _logger.Info($"Sending data request for deposit with id: '{depositId}', consumed units: {consumedUnits}, address: '{_consumerAddress}'.");
            var dataRequest = CreateDataRequest(depositDetails);
            providerPeer.SendSendDataRequest(dataRequest, consumedUnits);
            if (_logger.IsInfo) _logger.Info($"Sent data request for data header: '{dataRequest.DataHeaderId}', deposit: '{depositId}', consumed units: {consumedUnits}, address: '{_consumerAddress}'.");

            return dataRequest.DataHeaderId;
        }

        private async Task<bool> VerifyKycAsync(DataHeader dataHeader)
        {
            if (!dataHeader.KycRequired)
            {
                return true;
            }

            var headerId = dataHeader.Id;
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(headerId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was not found.");

                return false;
            }

            if (depositApproval.State != DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' has state: '{depositApproval.State}'.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was confirmed, required KYC is valid.");

            return true;
        }

        public async Task<Keccak> SendFinishSessionAsync(Keccak depositId)
        {
            var depositDetails = await GetDepositAsync(depositId);
            if (depositDetails is null)
            {
                if (_logger.IsInfo) _logger.Warn($"Deposit with id: '{depositId}' was not found.'");

                return null;
            }

            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return null;
            }

            if (!_providers.TryGetValue(session.ProviderNodeId, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{session.ProviderNodeId}' was not found.");
                
                return null;
            }

            providerPeer.SendFinishSession(depositId);

            return depositId;
        }

        public Task<Keccak> EnableDataStreamAsync(Keccak depositId, string[] subscriptions)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't enable data stream.");

                return Task.FromResult<Keccak>(null);
            }
            
            return ToggleDataStreamAsync(depositId, true, subscriptions);
        }

        public Task<Keccak> DisableDataStreamAsync(Keccak depositId)
            => ToggleDataStreamAsync(depositId, false);

        private async Task<Keccak> ToggleDataStreamAsync(Keccak depositId, bool enabled, string[] subscriptions = null)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                await SendDataRequestAsync(depositId);
                if (_logger.IsWarn) _logger.Warn($"Session for: '{depositId}' was not found.");
                return null;
            }

            if (!_providers.TryGetValue(session.ProviderNodeId, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{session.ProviderNodeId}' was not found.");
                return null;
            }
            
            var depositDetails = await GetDepositAsync(session.DepositId);
            if (depositDetails is null)
            {
                if (_logger.IsInfo) _logger.Info($"Cannot toggle data stream, deposit: '{session.DepositId}' was not found.");

                return null;
            }
            
            var dataHeaderId = depositDetails.DataHeader.Id;
            if (!_discoveredDataHeaders.TryGetValue(dataHeaderId, out var dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Available data header: '{dataHeaderId}' was not found.");

                return null;
            }

            if (!IsDataHeaderAvailable(dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Data header: '{dataHeaderId}' is unavailable, state: {dataHeader.State}.");

                return null;
            }

            if (enabled)
            {
                if (_logger.IsInfo) _logger.Info($"Sending enable data stream for: '{depositId}'.");
                providerPeer.SendEnableDataStream(depositId, subscriptions);
            }
            else
            {
                providerPeer.SendDisableDataStream(depositId);
            }

            return depositId;
        }

        public async Task SetEnabledDataStreamAsync(Keccak depositId, string[] subscriptions)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Enabling data stream for deposit: '{depositId}'.");
            session.EnableStream(subscriptions);
            await _sessionRepository.UpdateAsync(session);
        }

        public async Task SetDisabledDataStreamAsync(Keccak depositId)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Disabling data stream for deposit: '{depositId}'.");
            session.DisableStream();
            await _sessionRepository.UpdateAsync(session);
        }

        public async Task SendDataDeliveryReceiptAsync(DataDeliveryReceiptRequest request)
        {
            var depositId = request.DepositId;
            var (deposit, session) = TryGetDepositAndSession(depositId);
            if (deposit is null)
            {
                return;
            }

            if (session is null)
            {
                const int retries = 3;
                var retry = 0;
                while (retry < retries)
                {
                    retry++;
                    await Task.Delay(1000);
                    (_, session) = TryGetDepositAndSession(depositId);
                    if (session is null)
                    {
                        continue;
                    }
                    if (_logger.IsInfo) _logger.Info($"Found an active session: '{session.Id}' for deposit: '{deposit.Id}'.");
                    break;
                }
            }

            if (session is null)
            {
                return;
            }

            var providerAddress = deposit.DataHeader.Provider.Address;
            if (!_providers.TryGetValue(session.ProviderNodeId, out var providerPeer))
            {
                if (_logger.IsInfo) _logger.Info($"Provider: '{providerAddress}' was not found.");

                return;
            }


            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(request.Number),
                Rlp.Encode(_timestamp.EpochSeconds)));
            if (!_receiptRequestValidator.IsValid(request, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{providerPeer.NodeId}' sent an invalid data delivery receipt request.");
                var receipt = new DataDeliveryReceipt(StatusCodes.InvalidReceiptRequestRange,
                    session.ConsumedUnits, session.UnpaidUnits, new Signature(1, 1, 27));
                await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                    session.DataHeaderId, _nodePublicKey, request, receipt, _timestamp.EpochSeconds, false));
                await _sessionRepository.UpdateAsync(session);
                providerPeer.SendDataDeliveryReceipt(depositId, receipt);
                return;
            }

            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _dataDeliveryReceiptAbiSig,
                depositId.Bytes, new[] {request.UnitsRange.From, request.UnitsRange.To});
            var receiptHash = Keccak.Compute(abiHash);
            var signature = _wallet.Sign(receiptHash, _consumerAddress);
            var recoveredAddress = _ecdsa.RecoverPublicKey(signature, receiptHash)?.Address;
            if (_consumerAddress != recoveredAddress)
            {
                if (_logger.IsError) _logger.Error($"Signature failure when signing the receipt from provider: '{providerPeer.NodeId}', invalid recovered address.");
                var receipt = new DataDeliveryReceipt(StatusCodes.InvalidReceiptAddress,
                    session.ConsumedUnits, session.UnpaidUnits, new Signature(1, 1, 27));
                await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                    session.DataHeaderId, _nodePublicKey, request, receipt, _timestamp.EpochSeconds, false));
                await _sessionRepository.UpdateAsync(session);
                providerPeer.SendDataDeliveryReceipt(depositId, receipt);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Provider: '{providerPeer.NodeId}' sent a valid data delivery receipt request.");
            if (request.ReceiptsToMerge.Any())
            {
                if (_logger.IsInfo) _logger.Info($"Processing a merged receipt request for deposit: {session.DepositId}, session: '{session.Id} - units will not be updated.");
            }
            else
            {
                var paidUnits = request.UnitsRange.To - request.UnitsRange.From + 1;
                var unpaidUnits = session.UnpaidUnits > paidUnits ? session.UnpaidUnits - paidUnits : 0;
                session.SetUnpaidUnits(unpaidUnits);
                session.AddPaidUnits(paidUnits);
                if (request.IsSettlement)
                {
                    session.SetPaidUnits(paidUnits);
                    session.SettleUnits(paidUnits);
                    if (_logger.IsInfo) _logger.Info($"Settled {paidUnits} units for deposit: '{session.DepositId}', session: '{session.Id}'.");
                }
                
                await _sessionRepository.UpdateAsync(session);
            }
            
            if (_logger.IsInfo) _logger.Info($"Sending data delivery receipt for deposit: '{depositId}', range: [{request.UnitsRange.From}, {request.UnitsRange.To}].");
            var deliveryReceipt = new DataDeliveryReceipt(StatusCodes.Ok, session.ConsumedUnits,
                session.UnpaidUnits, signature);
            await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                session.DataHeaderId, _nodePublicKey, request, deliveryReceipt, _timestamp.EpochSeconds, false));
            providerPeer.SendDataDeliveryReceipt(depositId, deliveryReceipt);
            if (_logger.IsInfo) _logger.Info($"Sent data delivery receipt for deposit: '{depositId}', range: [{request.UnitsRange.From}, {request.UnitsRange.To}].");
        }

        public async Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason)
        {
            var depositDetails = await GetDepositAsync(ticket.DepositId);
            if (depositDetails is null)
            {
                return;
            }

            depositDetails.SetEarlyRefundTicket(ticket);
            await _depositRepository.UpdateAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Early refund claim for deposit: '{ticket.DepositId}', reason: '{reason}'.");
        }

        public async Task<Keccak> RequestDepositApprovalAsync(Keccak headerId, string kyc)
        {
            if (!_discoveredDataHeaders.TryGetValue(headerId, out var dataHeader))
            {
                if (_logger.IsInfo) _logger.Info($"Available data header: '{headerId}' was not found.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(kyc))
            {
                if (_logger.IsInfo) _logger.Info("KYC cannot be empty.");

                return null;
            }

            if (kyc.Length > 10000)
            {
                if (_logger.IsInfo) _logger.Info("Invalid KYC (over 10000 chars).");

                return null;
            }

            var providerPeer = GetProviderPeer(dataHeader.Provider.Address);
            if (providerPeer is null)
            {
                return null;
            }

            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(headerId), Rlp.Encode(_consumerAddress)));
            var approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                approval = new DepositApproval(id, headerId, dataHeader.Name, kyc, _consumerAddress,
                    dataHeader.Provider.Address, _timestamp.EpochSeconds);
                await _depositApprovalRepository.AddAsync(approval);
            }

            providerPeer.SendRequestDepositApproval(headerId, kyc);

            return id;
        }
        
        public async Task UpdateDepositApprovalsAsync(IReadOnlyList<DepositApproval> depositApprovals, Address provider)
        {
            if (!depositApprovals.Any())
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Received {depositApprovals.Count} deposit approvals from provider: '{provider}'.");
            var existingDepositApprovals = await _depositApprovalRepository.BrowseAsync(new GetConsumerDepositApprovals
            {
                Provider = provider,
                Results = int.MaxValue
            });
            foreach (var depositApproval in depositApprovals)
            {
                var existingDepositApproval = existingDepositApprovals.Items.SingleOrDefault(a => a.Id == depositApproval.Id);
                if (existingDepositApproval is null)
                {
                    await _depositApprovalRepository.AddAsync(depositApproval);
                    if (_logger.IsInfo) _logger.Info($"Added deposit approval for data header: '{depositApproval.HeaderId}'.");
                    continue;
                }

                if (existingDepositApproval.State == depositApproval.State)
                {
                    continue;
                }

                switch (depositApproval.State)
                {
                    case DepositApprovalState.Confirmed:
                        existingDepositApproval.Confirm();
                        await _depositApprovalRepository.UpdateAsync(existingDepositApproval);
                        if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{depositApproval.HeaderId}' was confirmed.");
                        break;
                    case DepositApprovalState.Rejected:
                        existingDepositApproval.Reject();
                        await _depositApprovalRepository.UpdateAsync(existingDepositApproval);
                        if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{depositApproval.HeaderId}' was rejected.");
                        break;
                }
            }
        }

        public async Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetConsumerDepositApprovals query)
            => await _depositApprovalRepository.BrowseAsync(query);

        public async Task ConfirmDepositApprovalAsync(Keccak headerId)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(headerId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was not found.");
                
                return;
            }

            if (depositApproval.State == DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was already confirmed.");
                
                return;
            }
            
            depositApproval.Confirm();
            await _depositApprovalRepository.UpdateAsync(depositApproval);
            if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was confirmed.");
        }

        public async Task RejectDepositApprovalAsync(Keccak headerId)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(headerId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was not found.");
                
                return;
            }

            if (depositApproval.State == DepositApprovalState.Rejected)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was already rejected.");
                
                return;
            }
            
            depositApproval.Reject();
            await _depositApprovalRepository.UpdateAsync(depositApproval);
            if (_logger.IsInfo) _logger.Info($"Deposit approval for data header: '{headerId}' was rejected.");
        }

        public async Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true)
        {
            if (!_providers.TryGetValue(provider.NodeId, out _))
            {
                if (_logger.IsInfo) _logger.Info($"Provider: '{provider.NodeId}' was not found.");

                return;
            }

            if (removePeer)
            {
                _providers.TryRemove(provider.NodeId, out _);
            }

            if (_providersWithCommonAddress.TryGetValue(provider.ProviderAddress, out var nodes) && removePeer)
            {
                nodes.TryRemove(provider.NodeId, out _);
                if (nodes.Count == 0)
                {
                    _providersWithCommonAddress.TryRemove(provider.ProviderAddress, out _);
                }
            }
            
            var depositId = session.DepositId;
            var consumerSession = GetActiveSession(depositId);
            if (consumerSession is null)
            {
                return;
            }
            
            _sessions.TryRemove(session.DepositId, out _);
            var timestamp = session.FinishTimestamp;
            consumerSession.Finish(session.State, timestamp);
            await _sessionRepository.UpdateAsync(consumerSession);
            if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
        }

        public async Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true)
        {
            if (_logger.IsInfo) _logger.Info($"Finishing {_sessions.Count} session(s) with provider: '{provider.ProviderAddress}'.");
            if (!_providers.TryGetValue(provider.NodeId, out var providerPeer))
            {
                if (_logger.IsInfo) _logger.Info($"Provider: '{provider.NodeId}' was not found.");

                return;
            }
            
            if (removePeer)
            {
                _providers.TryRemove(provider.NodeId, out _);
            }

            if (_providersWithCommonAddress.TryGetValue(provider.ProviderAddress, out var nodes) && removePeer)
            {
                nodes.TryRemove(provider.NodeId, out _);
                if (nodes.Count == 0)
                {
                    _providersWithCommonAddress.TryRemove(provider.ProviderAddress, out _);
                }
            }

            var timestamp = _timestamp.EpochSeconds;
            foreach (var (_, session) in _sessions)
            {
                if (!providerPeer.ProviderAddress.Equals(session.ProviderAddress))
                {
                    if (_logger.IsInfo) _logger.Info($"Provider: '{provider.ProviderAddress}' address is invalid.");

                    continue;
                }

                var depositId = session.DepositId;
                if (_logger.IsInfo) _logger.Info($"Finishing a session: '{session.Id}' for deposit: '{depositId}'.");
                _sessions.TryRemove(session.DepositId, out _);
                session.Finish(SessionState.ProviderDisconnected, timestamp);
                await _sessionRepository.UpdateAsync(session);
                if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
            }
        }
        
        private INdmPeer GetProviderPeer(Address address)
        {
            if (!_providersWithCommonAddress.TryGetValue(address, out var nodes) || nodes.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider nodes were not found for address: '{address}'.");

                return null;
            }
            
            //TODO: Select a random node and add load balancing in the future.
            var nodeId = nodes.First();
            if (!_providers.TryGetValue(nodeId.Key, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{nodeId}' was not found.");
                
                return null;
            }

            return providerPeer;
        }

        private async Task ClaimEarlyRefundAsync(DepositDetails depositDetails)
        {
            var depositId = depositDetails.Deposit.Id;
            var dataRequest = CreateDataRequest(depositDetails);
            var ticket = depositDetails.EarlyRefundTicket;
            var earlyRefundClaim = new EarlyRefundClaim(ticket.DepositId, depositDetails.DataHeader.Id,
                dataRequest.Units, dataRequest.Value, dataRequest.ExpiryTime, dataRequest.Pepper,
                depositDetails.DataHeader.Provider.Address,
                ticket.ClaimableAfter, ticket.Signature, _consumerAddress);
            var transactionHash = _refundService.ClaimEarlyRefund(_consumerAddress, earlyRefundClaim);
            var (receipt, transaction) = _blockchainBridge.GetTransaction(depositDetails.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositDetails.Id}' to claim an early refund.");
                return;
            }
            
            var confirmations = _blockchainBridge.Head.Number - receipt.BlockNumber;
            if (_logger.IsInfo) _logger.Info($"Deposit: '{depositDetails.Id}' has {confirmations} confirmations (required at least {_blockConfirmations}) for transaction hash: '{transactionHash}' to claim an early refund.");
            if (confirmations < _blockConfirmations)
            {
                return;
            }
            
            depositDetails.SetRefundClaimed(transactionHash);
            await _depositRepository.UpdateAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Claimed an early refund for deposit: '{depositId}', transaction hash: '{transactionHash}'.");
        }

        private async Task ClaimRefundAsync(DepositDetails depositDetails)
        {
            var depositId = depositDetails.Deposit.Id;
            var dataRequest = CreateDataRequest(depositDetails);
            var provider = depositDetails.DataHeader.Provider.Address;
            var refundClaim = new RefundClaim(depositId, depositDetails.DataHeader.Id, dataRequest.Units,
                dataRequest.Value, dataRequest.ExpiryTime, dataRequest.Pepper, provider, _consumerAddress);
            var transactionHash = _refundService.ClaimRefund(_consumerAddress, refundClaim);
            var (receipt, transaction) = _blockchainBridge.GetTransaction(depositDetails.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositDetails.Id}' to claim a refund.");
                return;
            }
            
            var confirmations = _blockchainBridge.Head.Number - receipt.BlockNumber;
            if (_logger.IsInfo) _logger.Info($"Deposit: '{depositDetails.Id}' has {confirmations} confirmations (required at least {_blockConfirmations}) for transaction hash: '{transactionHash}' to claim a refund.");
            if (confirmations < _blockConfirmations)
            {
                return;
            }
            
            depositDetails.SetRefundClaimed(transactionHash);
            await _depositRepository.UpdateAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Claimed a refund for deposit: '{depositId}', transaction hash: '{transactionHash}'.");
        }

        private (DepositDetails deposit, ConsumerSession session) TryGetDepositAndSession(Keccak depositId)
        {
            if (!_deposits.TryGetValue(depositId, out var depositDetails))
            {
                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' was not found.");

                return (null, null);
            }
            
            var session = GetActiveSession(depositId);
            return (session is null) ? (depositDetails, null) : (depositDetails, session);
        }

        private ConsumerSession GetActiveSession(Keccak depositId)
        {
            if (_sessions.TryGetValue(depositId, out var session))
            {
                return session;
            }

            if (_logger.IsInfo) _logger.Info($"Active session for deposit: '{depositId}' was not found.");
            
            return null;
        }

        private bool IsDataHeaderAvailable(DataHeader dataHeader)
            => dataHeader.State == DataHeaderState.Published || dataHeader.State == DataHeaderState.UnderMaintenance;

        private DataRequest CreateDataRequest(DepositDetails deposit)
        {
            var hash = Keccak.Compute(_nodePublicKey.Bytes);
            var signature = _wallet.Sign(hash, _consumerAddress);

            return new DataRequest(deposit.DataHeader.Id, deposit.Deposit.Units, deposit.Deposit.Value,
                deposit.Deposit.ExpiryTime, deposit.Pepper, deposit.DataHeader.Provider.Address, _consumerAddress,
                signature);
        }

        private void SetActiveSession(ConsumerSession session)
        {
            _sessions.TryRemove(session.DepositId, out _);            
            _sessions.TryAdd(session.DepositId, session);
        }
    }
}