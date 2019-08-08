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
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Abi;
using Nethermind.Blockchain;
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
        private const int TryClaimRefundsIntervalSeconds = 60;
        private readonly ConcurrentDictionary<Keccak, ConsumerSession> _sessions =
            new ConcurrentDictionary<Keccak, ConsumerSession>();

        private readonly ConcurrentDictionary<Keccak, DataAsset> _discoveredDataAssets =
            new ConcurrentDictionary<Keccak, DataAsset>();

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
        private readonly IBlockProcessor _blockProcessor;
        private Address _consumerAddress;
        private readonly PublicKey _nodePublicKey;
        private readonly ITimestamper _timestamper;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly uint _requiredBlockConfirmations;
        private readonly ILogger _logger;
        private readonly IEcdsa _ecdsa;
        private bool _accountLocked;
        private readonly Timer _timer;
        private long _currentBlockTimestamp;

        public ConsumerService(IConfigManager configManager, string configId,
            IDepositDetailsRepository depositRepository, IConsumerDepositApprovalRepository depositApprovalRepository,
            IProviderRepository providerRepository, IReceiptRepository receiptRepository,
            IConsumerSessionRepository sessionRepository, IWallet wallet, IAbiEncoder abiEncoder, IEcdsa ecdsa,
            ICryptoRandom cryptoRandom, IDepositService depositService,
            IReceiptRequestValidator receiptRequestValidator, IRefundService refundService,
            IBlockchainBridge blockchainBridge, IBlockProcessor blockProcessor, Address consumerAddress,
            PublicKey nodePublicKey, ITimestamper timestamper, IConsumerNotifier consumerNotifier,
            uint requiredBlockConfirmations, ILogManager logManager)
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
            _ecdsa = ecdsa;
            _cryptoRandom = cryptoRandom;
            _depositService = depositService;
            _receiptRequestValidator = receiptRequestValidator;
            _refundService = refundService;
            _blockchainBridge = blockchainBridge;
            _blockProcessor = blockProcessor;
            _consumerAddress = consumerAddress ?? Address.Zero;
            _nodePublicKey = nodePublicKey;
            _timestamper = timestamper;
            _consumerNotifier = consumerNotifier;
            _requiredBlockConfirmations = requiredBlockConfirmations;
            _logger = logManager.GetClassLogger();
            _wallet.AccountLocked += OnAccountLocked;
            _wallet.AccountUnlocked += OnAccountUnlocked;
            _accountLocked = !_wallet.IsUnlocked(_consumerAddress);
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            _timer = new Timer(TryClaimRefundsIntervalSeconds * 1000);
            _timer.Elapsed += TimerOnElapsed;
            _timer.Start();
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_logger.IsInfo) _logger.Info("Verifying whether any refunds might be claimed...");
            _depositRepository.BrowseAsync(new GetDeposits
                {
                    Results = int.MaxValue,
                    EligibleToRefund = true,
                    CurrentBlockTimestamp = _currentBlockTimestamp
                })
                .ContinueWith(async t =>
                {
                    if (t.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Fetching the deposits has failed.", t.Exception);
                        return;
                    }

                    await TryClaimRefundsAsync(t.Result.Items);
                });
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            Interlocked.Exchange(ref _currentBlockTimestamp, (long) e.Block.Timestamp);
            _consumerNotifier.SendBlockProcessedAsync(e.Block.Number);
            _depositRepository.BrowseAsync(new GetDeposits
            {
                OnlyUnconfirmed = true,
                OnlyNotRejected = true,
                Results = int.MaxValue
            }).ContinueWith(async t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Fetching the deposits has failed.", t.Exception);
                    return;
                }

                await TryConfirmDepositsAsync(t.Result.Items);
            });
        }

        private async Task TryConfirmDepositsAsync(IEnumerable<DepositDetails> deposits)
        {
            foreach (var deposit in deposits)
            {
                await TryConfirmDepositAsync(deposit).ContinueWith(verifyDepositTask =>
                {
                    if (verifyDepositTask.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Confirming a deposit with id: '{deposit.Id}' has failed.",
                            verifyDepositTask.Exception);
                    }
                });
            }
        }

        private void OnAccountUnlocked(object sender, AccountUnlockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }

            _accountLocked = false;
            if (_logger.IsInfo) _logger.Info($"Unlocked a consumer account: '{e.Address}', data streams can be enabled.");
        }

        private void OnAccountLocked(object sender, AccountLockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }
            
            _accountLocked = true;
            _consumerNotifier.SendConsumerAccountLockedAsync(e.Address);
            if (_logger.IsInfo) _logger.Info($"Locked a consumer account: '{e.Address}', all of the existing data streams will be disabled.");

            var disableStreamTasks = from session in _sessions.Values
                from client in session.Clients
                select DisableDataStreamAsync(session.DepositId, client.Id);

            Task.WhenAll(disableStreamTasks).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error("Disabling the data stream has failed.", t.Exception);
                }
            });
        }

        private async Task TryConfirmDepositAsync(DepositDetails deposit)
        {
            if (deposit.Confirmed || deposit.Rejected)
            {
                return;
            }

            var head = _blockchainBridge.Head;
            var transactionHash = deposit.TransactionHash;
            var (receipt, transaction) = _blockchainBridge.GetTransaction(deposit.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{deposit.Id}' to be confirmed.");
                return;
            }

            var (confirmations, rejected) = await VerifyDepositConfirmationsAsync(deposit, receipt, head.Hash);
            if (rejected)
            {
                deposit.Reject();
                await _depositRepository.UpdateAsync(deposit);
                await _consumerNotifier.SendDepositRejectedAsync(deposit.Id);
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has {confirmations} confirmations (required at least {_requiredBlockConfirmations}) for transaction hash: '{transactionHash}' to be confirmed.");
            var confirmed = confirmations >= _requiredBlockConfirmations;
            if (confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Deposit.Id}' has been confirmed.");
            }
            
            if (confirmations != deposit.Confirmations || confirmed)
            {
                deposit.SetConfirmations(confirmations);
                await _depositRepository.UpdateAsync(deposit);
            }

            await _consumerNotifier.SendDepositConfirmationsStatusAsync(deposit.Id, deposit.DataAsset.Name,
                confirmations, _requiredBlockConfirmations, deposit.ConfirmationTimestamp, confirmed);
        }

        private async Task<(uint confirmations, bool rejected)> VerifyDepositConfirmationsAsync(DepositDetails deposit,
            TxReceipt receipt, Keccak headHash)
        {
            var confirmations = 0u;
            var block = _blockchainBridge.FindBlock(headHash);
            while (confirmations < _requiredBlockConfirmations)
            {
                if (block is null)
                {
                    if (_logger.IsWarn) _logger.Warn("Block was not found.");
                    return (0, false);
                }

                var confirmationTimestamp = _depositService.VerifyDeposit(deposit.Consumer, deposit.Id, block.Header);
                if (confirmationTimestamp > 0)
                {
                    confirmations++;
                    if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has been confirmed in block number: {block.Number}, hash: '{block.Hash}', transaction hash: '{deposit.TransactionHash}', timestamp: {confirmationTimestamp}.");
                    if (deposit.ConfirmationTimestamp == 0)
                    {
                        deposit.SetConfirmationTimestamp(confirmationTimestamp);
                        await _depositRepository.UpdateAsync(deposit);
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Id}' has not returned confirmation timestamp from the contract call yet.'");
                    return (0, false);
                }
                
                if (confirmations == _requiredBlockConfirmations)
                {
                    break;
                }

                if (receipt.BlockHash == block.Hash || block.Number <= receipt.BlockNumber)
                {
                    break;
                }

                block = _blockchainBridge.FindBlock(block.ParentHash);
            }

            var blocksDifference = _blockchainBridge.Head.Number - receipt.BlockNumber;
            if (blocksDifference >= _requiredBlockConfirmations && confirmations < _requiredBlockConfirmations)
            {
                if (_logger.IsError) _logger.Error($"Deposit: '{deposit.Id}' has been rejected - missing confirmation in block number: {block.Number}, hash: {block.Hash}' (transaction hash: '{deposit.TransactionHash}').");
                return (confirmations, true);
            }

            return (confirmations, false);
        }
        
        private async Task TryClaimRefundsAsync(IEnumerable<DepositDetails> deposits)
        {
            foreach (var deposit in deposits)
            {
                await TryClaimRefundAsync(deposit);
            }
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

            var previousAddress = _consumerAddress;
            if (_logger.IsInfo) _logger.Info($"Changing consumer address: '{previousAddress}' -> '{address}'...");
            _consumerAddress = address;
            _accountLocked = !_wallet.IsUnlocked(_consumerAddress);
            AddressChanged?.Invoke(this, new AddressChangedEventArgs(previousAddress, _consumerAddress));
            var config = await _configManager.GetAsync(_configId);
            config.ConsumerAddress = _consumerAddress.ToString();
            await _configManager.UpdateAsync(config);
            foreach (var (_, provider) in _providers)
            {
                provider.ChangeHostConsumerAddress(_consumerAddress);
                provider.SendConsumerAddressChanged(_consumerAddress);
                await FinishSessionsAsync(provider, false);
            }
            
            await _consumerNotifier.SendConsumerAddressChangedAsync(address, previousAddress);
            if (_logger.IsInfo) _logger.Info($"Changed consumer address: '{previousAddress}' -> '{address}'.");
        }

        public async Task ChangeProviderAddressAsync(INdmPeer peer, Address address)
        {
            if (peer.ProviderAddress == address)
            {
                return;
            }
            
            var previousAddress = peer.ProviderAddress;
            if (_logger.IsInfo) _logger.Info($"Changing provider address: '{previousAddress}' -> '{address}' for peer: '{peer.NodeId}'.");
            _providersWithCommonAddress.TryRemove(peer.ProviderAddress, out _);
            peer.ChangeProviderAddress(address);
            AddProviderNodes(peer);
            await _consumerNotifier.SendProviderAddressChangedAsync(address, previousAddress);
            if (_logger.IsInfo) _logger.Info($"Changed provider address: '{previousAddress}' -> '{address}'.");
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

        public void AddDiscoveredDataAsset(DataAsset dataAsset, INdmPeer peer)
        {
            _discoveredDataAssets.TryAdd(dataAsset.Id, dataAsset);
        }

        public void AddDiscoveredDataAssets(DataAsset[] dataAssets, INdmPeer peer)
        {
            for (var i = 0; i < dataAssets.Length; i++)
            {
                var dataAsset = dataAssets[i];
                _discoveredDataAssets.TryAdd(dataAsset.Id, dataAsset);
            }
        }

        public void ChangeDataAssetState(Keccak dataAssetId, DataAssetState state)
        {
            if (!_discoveredDataAssets.TryGetValue(dataAssetId, out var dataAsset))
            {
                return;
            }
            
            dataAsset.SetState(state);
            _consumerNotifier.SendDataAssetStateChangedAsync(dataAssetId, dataAsset.Name, state);
            if (_logger.IsInfo) _logger.Info($"Changed discovered data asset: '{dataAssetId}' state to: '{state}'.");
        }

        public void RemoveDiscoveredDataAsset(Keccak dataAssetId)
        {
            if (!_discoveredDataAssets.TryRemove(dataAssetId, out var dataAsset))
            {
                return;
            }

            _consumerNotifier.SendDataAssetRemovedAsync(dataAssetId, dataAsset.Name);
        }

        public async Task StartSessionAsync(Session session, INdmPeer provider)
        {
            if (!_providers.TryGetValue(provider.NodeId, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', provider: '{provider.NodeId}' was not found.");

                return;
            }

            var depositDetails = await GetDepositAsync(session.DepositId);
            if (depositDetails is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', deposit: '{session.DepositId}' was not found.");

                return;
            }

            var dataAssetId = depositDetails.DataAsset.Id;
            if (!_discoveredDataAssets.TryGetValue(dataAssetId, out var dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Available data asset: '{dataAssetId}' was not found.");

                return;
            }

            if (!IsDataAssetAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAssetId}' is unavailable, state: {dataAsset.State}.");

                return;
            }

            if (!providerPeer.ProviderAddress.Equals(depositDetails.DataAsset.Provider.Address))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', provider address (peer): '{providerPeer.ProviderAddress}' doesn't equal the address from data asset: '{depositDetails.DataAsset.Provider.Address}'.");

                return;
            }

            if (!_discoveredDataAssets.TryGetValue(depositDetails.DataAsset.Id, out _))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', discovered data asset: '{depositDetails.DataAsset.Id}' was not found.");

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
            var upfrontUnits = (uint) (depositDetails.DataAsset.Rules.UpfrontPayment?.Value ?? 0);
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
            
            if (depositDetails.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                var unpaidTimeUnits = (uint) consumerSession.StartTimestamp - depositDetails.ConfirmationTimestamp;
                consumerSession.AddUnpaidUnits(unpaidTimeUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: '{unpaidTimeUnits}' for deposit: '{session.DepositId}' based on time.");
            }

            SetActiveSession(consumerSession);
            await _sessionRepository.AddAsync(consumerSession);
            await _consumerNotifier.SendSessionStartedAsync(session.DepositId, session.Id);
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
            switch (deposit.DataAsset.UnitType)
            {
                case DataAssetUnitType.Time:
                    var now = (uint) _timestamper.EpochSeconds;
                    var currentlyConsumedUnits = now - deposit.ConfirmationTimestamp;
                    var currentlyUnpaidUnits = currentlyConsumedUnits > session.PaidUnits
                        ? currentlyConsumedUnits - session.PaidUnits
                        : 0;
                    session.SetConsumedUnits((uint)(now - session.StartTimestamp));
                    session.SetUnpaidUnits(currentlyUnpaidUnits);
                    break;
                case DataAssetUnitType.Unit:
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
            await _consumerNotifier.SendDataAvailabilityChangedAsync(depositId, session.Id, dataAvailability);
        }

        public async Task<Keccak> MakeDepositAsync(Keccak assetId, uint units, UInt256 value)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't make a deposit.");
                
                return null;
            }
            
            if (!_discoveredDataAssets.TryGetValue(assetId, out var dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Available data asset: '{assetId}' was not found.");

                return null;
            }

            if (!IsDataAssetAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{assetId}' is unavailable, state: {dataAsset.State}.");

                return null;
            }

            if (!(await VerifyKycAsync(dataAsset)))
            {
                return null;
            }

            var providerAddress = dataAsset.Provider.Address;
            if (!_providersWithCommonAddress.TryGetValue(providerAddress, out var nodes) || nodes.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider nodes were not found for address: '{providerAddress}'.");

                return null;
            }

            if (dataAsset.MinUnits > units || dataAsset.MaxUnits < units)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid data request units: '{units}', min: '{dataAsset.MinUnits}', max: '{dataAsset.MaxUnits}'.");

                return null;
            }

            var unitsValue = units * dataAsset.UnitPrice;
            if (units * dataAsset.UnitPrice != value)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid data request value: '{value}', while it should be: '{unitsValue}'.");

                return null;
            }

            var now = (uint) _timestamper.EpochSeconds;
            var expiryTime = now + (uint) dataAsset.Rules.Expiry.Value;
            expiryTime += dataAsset.UnitType == DataAssetUnitType.Unit ? 0 : units;
            var pepper = _cryptoRandom.GenerateRandomBytes(16);
            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _depositAbiSig, assetId.Bytes,
                units, value, expiryTime, pepper, dataAsset.Provider.Address, _consumerAddress);
            var depositId = Keccak.Compute(abiHash);
            var deposit = new Deposit(depositId, units, expiryTime, value);
            var transactionHash = _depositService.MakeDeposit(_consumerAddress, deposit);
            var depositDetails = new DepositDetails(deposit, dataAsset, _consumerAddress, pepper, now,
                transactionHash, requiredConfirmations: _requiredBlockConfirmations);
            await _depositRepository.AddAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Sent a deposit with id: '{depositId}', transaction hash: '{transactionHash}' for data asset: '{assetId}', address: '{_consumerAddress}'.");
                
            return depositId;
        }

        public IReadOnlyList<Address> GetConnectedProviders()
            => _providers.Values.Select(p => p.ProviderAddress).ToArray();

        public IReadOnlyList<ConsumerSession> GetActiveSessions() => _sessions.Values.ToArray();
        
        public IReadOnlyList<DataAsset> GetDiscoveredDataAssets()
            => _discoveredDataAssets.Values.Where(h => h.State == DataAssetState.Published ||
                                                        h.State == DataAssetState.UnderMaintenance).ToArray();

        public Task<IReadOnlyList<ProviderInfo>> GetKnownProvidersAsync()
            => _providerRepository.GetProvidersAsync();

        public Task<IReadOnlyList<DataAssetInfo>> GetKnownDataAssetsAsync()
            => _providerRepository.GetDataAssetsAsync();
        
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

        public async Task<DataRequestResult> SendDataRequestAsync(Keccak depositId)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't send a data request.");

                return DataRequestResult.ConsumerAccountLocked;
            }
            
            if (!_deposits.TryGetValue(depositId, out var depositDetails))
            {
                depositDetails = await GetDepositAsync(depositId);
                if (depositDetails is null)
                {
                    if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was not found.'");
                    return DataRequestResult.DepositNotFound;
                }

                _deposits.TryAdd(depositId, depositDetails);
            }

            if (!(await VerifyKycAsync(depositDetails.DataAsset)))
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' has unconfirmed KYC.'");
                
                return DataRequestResult.KycUnconfirmed;
            }

            if (!depositDetails.Confirmed)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' is not confirmed.'");

                return DataRequestResult.DepositUnconfirmed;
            }

            var providerPeer = GetProviderPeer(depositDetails.DataAsset.Provider.Address);
            if (providerPeer is null)
            {
                return DataRequestResult.ProviderNotFound;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = depositId,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            if (_logger.IsInfo) _logger.Info($"Sending data request for deposit with id: '{depositId}', consumed units: {consumedUnits}, address: '{_consumerAddress}'.");
            var dataRequest = CreateDataRequest(depositDetails);
            var result = await providerPeer.SendDataRequestAsync(dataRequest, consumedUnits);
            if (_logger.IsInfo) _logger.Info($"Received data request result: '{result}' for data asset: '{dataRequest.DataAssetId}', deposit: '{depositId}', consumed units: {consumedUnits}, address: '{_consumerAddress}'.");
            await _consumerNotifier.SendDataRequestResultAsync(depositId, result);
            
            return result;
        }

        private async Task<bool> VerifyKycAsync(DataAsset dataAsset)
        {
            if (!dataAsset.KycRequired)
            {
                return true;
            }

            var assetId = dataAsset.Id;
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit approval for data asset: '{assetId}' was not found.");

                return false;
            }

            if (depositApproval.State != DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' has state: '{depositApproval.State}'.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was confirmed, required KYC is valid.");

            return true;
        }

        public async Task<Keccak> SendFinishSessionAsync(Keccak depositId)
        {
            var depositDetails = await GetDepositAsync(depositId);
            if (depositDetails is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' was not found.'");

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

        public Task<Keccak> EnableDataStreamAsync(Keccak depositId, string client, string[] args)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_consumerAddress}' is locked, can't enable data stream.");

                return Task.FromResult<Keccak>(null);
            }
            
            return ToggleDataStreamAsync(depositId, true, client, args);
        }

        public Task<Keccak> DisableDataStreamAsync(Keccak depositId, string client)
            => ToggleDataStreamAsync(depositId, false, client);

        public async Task<Keccak> DisableDataStreamsAsync(Keccak depositId)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Disabling all data streams for deposit: '{depositId}'.");

            var disableStreamTasks = from client in session.Clients
                select DisableDataStreamAsync(session.DepositId, client.Id);
            await Task.WhenAll(disableStreamTasks);
            if (_logger.IsInfo) _logger.Info($"Disabled all data streams for deposit: '{depositId}'.");

            return depositId;
        }

        private async Task<Keccak> ToggleDataStreamAsync(Keccak depositId, bool enabled, string client,
            string[] args = null)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Session for deposit: '{depositId}' was not found.");
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
                if (_logger.IsWarn) _logger.Warn($"Cannot toggle data stream, deposit: '{session.DepositId}' was not found.");

                return null;
            }
            
            var dataAssetId = depositDetails.DataAsset.Id;
            if (!_discoveredDataAssets.TryGetValue(dataAssetId, out var dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Available data asset: '{dataAssetId}' was not found.");

                return null;
            }

            if (!IsDataAssetAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAssetId}' is unavailable, state: {dataAsset.State}.");

                return null;
            }

            if (enabled)
            {
                if (_logger.IsInfo) _logger.Info($"Sending enable data stream for deposit: '{depositId}', client: '{client}'.");
                providerPeer.SendEnableDataStream(depositId, client, args);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Sending disable data stream for deposit: '{depositId}', client: '{client}'.");
                providerPeer.SendDisableDataStream(depositId, client);
            }

            return depositId;
        }

        public async Task SetEnabledDataStreamAsync(Keccak depositId, string client, string[] args)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return;
            }

            session.EnableStream(client, args);
            await _sessionRepository.UpdateAsync(session);
            await _consumerNotifier.SendDataStreamEnabledAsync(depositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Enabled data stream for deposit: '{depositId}', client: '{client}', session: '{session.Id}'.'");
        }

        public async Task SetDisabledDataStreamAsync(Keccak depositId, string client)
        {
            var session = GetActiveSession(depositId);
            if (session is null)
            {
                return;
            }
            
            session.DisableStream(client);
            await _sessionRepository.UpdateAsync(session);
            await _consumerNotifier.SendDataStreamDisabledAsync(depositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Disabled data stream for deposit: '{depositId}', client: '{client}', session: '{session.Id}'.");
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
                    await Task.Delay(3000);
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

            var providerAddress = deposit.DataAsset.Provider.Address;
            if (!_providers.TryGetValue(session.ProviderNodeId, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{providerAddress}' was not found.");

                return;
            }


            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(request.Number),
                Rlp.Encode(_timestamper.EpochSeconds)));
            if (!_receiptRequestValidator.IsValid(request, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{providerPeer.NodeId}' sent an invalid data delivery receipt request.");
                var receipt = new DataDeliveryReceipt(StatusCodes.InvalidReceiptRequestRange,
                    session.ConsumedUnits, session.UnpaidUnits, new Signature(1, 1, 27));
                await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                    session.DataAssetId, _nodePublicKey, request, receipt, _timestamper.EpochSeconds, false));
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
                    session.DataAssetId, _nodePublicKey, request, receipt, _timestamper.EpochSeconds, false));
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
                session.DataAssetId, _nodePublicKey, request, deliveryReceipt, _timestamper.EpochSeconds, false));
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

        public async Task<Keccak> RequestDepositApprovalAsync(Keccak assetId, string kyc)
        {
            if (!_discoveredDataAssets.TryGetValue(assetId, out var dataAsset))
            {
                if (_logger.IsError) _logger.Error($"Available data asset: '{assetId}' was not found.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(kyc))
            {
                if (_logger.IsError) _logger.Error("KYC cannot be empty.");

                return null;
            }

            if (kyc.Length > 100000)
            {
                if (_logger.IsError) _logger.Error("Invalid KYC (over 100000 chars).");

                return null;
            }

            var providerPeer = GetProviderPeer(dataAsset.Provider.Address);
            if (providerPeer is null)
            {
                return null;
            }

            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(_consumerAddress)));
            var approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                approval = new DepositApproval(id, assetId, dataAsset.Name, kyc, _consumerAddress,
                    dataAsset.Provider.Address, _timestamper.EpochSeconds);
                await _depositApprovalRepository.AddAsync(approval);
            }

            providerPeer.SendRequestDepositApproval(assetId, kyc);

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
                    if (_logger.IsInfo) _logger.Info($"Added deposit approval for data asset: '{depositApproval.AssetId}'.");
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
                        await _consumerNotifier.SendDepositApprovalConfirmedAsync(depositApproval.AssetId,
                            depositApproval.AssetName);
                        if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{depositApproval.AssetId}' was confirmed.");
                        break;
                    case DepositApprovalState.Rejected:
                        existingDepositApproval.Reject();
                        await _depositApprovalRepository.UpdateAsync(existingDepositApproval);
                        await _consumerNotifier.SendDepositApprovalRejectedAsync(depositApproval.AssetId,
                            depositApproval.AssetName);
                        if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{depositApproval.AssetId}' was rejected.");
                        break;
                }
            }
        }

        public Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason)
            => _consumerNotifier.SendDataInvalidAsync(depositId, reason);

        public async Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetConsumerDepositApprovals query)
            => await _depositApprovalRepository.BrowseAsync(query);

        public async Task ConfirmDepositApprovalAsync(Keccak assetId)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}' was not found.");
                
                return;
            }

            if (depositApproval.State == DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was already confirmed.");
                
                return;
            }
            
            depositApproval.Confirm();
            await _depositApprovalRepository.UpdateAsync(depositApproval);
            await _consumerNotifier.SendDepositApprovalConfirmedAsync(depositApproval.AssetId,
                depositApproval.AssetName);
            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was confirmed.");
        }

        public async Task RejectDepositApprovalAsync(Keccak assetId)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(_consumerAddress)));
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}' was not found.");
                
                return;
            }

            if (depositApproval.State == DepositApprovalState.Rejected)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was already rejected.");
                
                return;
            }
            
            depositApproval.Reject();
            await _depositApprovalRepository.UpdateAsync(depositApproval);
            await _consumerNotifier.SendDepositApprovalRejectedAsync(depositApproval.AssetId,
                depositApproval.AssetName);
            if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}' was rejected.");
        }

        public async Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true)
        {
            if (!_providers.TryGetValue(provider.NodeId, out _))
            {
                if (_logger.IsInfo) _logger.Info($"Provider node: '{provider.NodeId}' was not found.");
                return;
            }

            if (removePeer)
            {
                _providers.TryRemove(provider.NodeId, out _);
            }

            if (provider.ProviderAddress is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{provider.NodeId}' has no address assigned.");
                return;
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
            await _consumerNotifier.SendSessionFinishedAsync(session.DepositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
        }

        public async Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true)
        {
            if (_logger.IsInfo) _logger.Info($"Finishing {_sessions.Count} session(s) with provider: '{provider.ProviderAddress}'.");
            if (!_providers.TryGetValue(provider.NodeId, out var providerPeer))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{provider.NodeId}' was not found.");
                return;
            }
            
            if (removePeer)
            {
                _providers.TryRemove(provider.NodeId, out _);
            }
            
            if (provider.ProviderAddress is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{provider.NodeId}' has no address assigned.");
                return;
            }

            if (_providersWithCommonAddress.TryGetValue(provider.ProviderAddress, out var nodes) && removePeer)
            {
                nodes.TryRemove(provider.NodeId, out _);
                if (nodes.Count == 0)
                {
                    _providersWithCommonAddress.TryRemove(provider.ProviderAddress, out _);
                }
            }

            var timestamp = _timestamper.EpochSeconds;
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
                await _consumerNotifier.SendSessionFinishedAsync(session.DepositId, session.Id);
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
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{nodeId}' was not found.");
                return null;
            }

            return providerPeer;
        }

        private async Task ClaimEarlyRefundAsync(DepositDetails depositDetails)
        {
            var depositId = depositDetails.Deposit.Id;
            var dataRequest = CreateDataRequest(depositDetails);
            var ticket = depositDetails.EarlyRefundTicket;
            var earlyRefundClaim = new EarlyRefundClaim(ticket.DepositId, depositDetails.DataAsset.Id,
                dataRequest.Units, dataRequest.Value, dataRequest.ExpiryTime, dataRequest.Pepper,
                depositDetails.DataAsset.Provider.Address,
                ticket.ClaimableAfter, ticket.Signature, _consumerAddress);
            var transactionHash = _refundService.ClaimEarlyRefund(_consumerAddress, earlyRefundClaim);
            var (receipt, transaction) = _blockchainBridge.GetTransaction(depositDetails.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositDetails.Id}' to claim an early refund.");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Trying to claim an early refund (transaction hash: '{transactionHash}') for deposit: '{depositId}'.");
            var (confirmations, blockFound) = GetTransactionConfirmations(receipt);
            if (!blockFound)
            {
                if (_logger.IsWarn) _logger.Warn($"Block number: {receipt.BlockNumber}, hash: '{receipt.BlockHash}' was not found for transaction hash: '{receipt.TxHash}' - an early refund for deposit: '{depositId}' will not be claimed.");
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"An early refund claim (transaction hash: '{transactionHash}') for deposit: '{depositId}' has {confirmations} confirmations (required at least {_requiredBlockConfirmations}).");
            if (confirmations < _requiredBlockConfirmations)
            {
                return;
            }
            
            depositDetails.SetRefundClaimed(transactionHash);
            await _depositRepository.UpdateAsync(depositDetails);
            await _consumerNotifier.SendClaimedEarlyRefundAsync(depositId, depositDetails.DataAsset.Name, transactionHash);
            if (_logger.IsInfo) _logger.Info($"Claimed an early refund for deposit: '{depositId}', transaction hash: '{transactionHash}'.");
        }

        private async Task ClaimRefundAsync(DepositDetails depositDetails)
        {
            var depositId = depositDetails.Deposit.Id;
            var dataRequest = CreateDataRequest(depositDetails);
            var provider = depositDetails.DataAsset.Provider.Address;
            var refundClaim = new RefundClaim(depositId, depositDetails.DataAsset.Id, dataRequest.Units,
                dataRequest.Value, dataRequest.ExpiryTime, dataRequest.Pepper, provider, _consumerAddress);
            var transactionHash = _refundService.ClaimRefund(_consumerAddress, refundClaim);
            var (receipt, transaction) = _blockchainBridge.GetTransaction(depositDetails.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositDetails.Id}' to claim a refund.");
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Trying to claim a refund (transaction hash: '{transactionHash}') for deposit: '{depositId}'.");
            var (confirmations, blockFound) = GetTransactionConfirmations(receipt);
            if (!blockFound)
            {
                if (_logger.IsWarn) _logger.Warn($"Block number: {receipt.BlockNumber}, hash: '{receipt.BlockHash}' was not found for transaction hash: '{receipt.TxHash}' - a refund for deposit: '{depositId}' will not be claimed.");
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"A refund claim (transaction hash: '{transactionHash}') for deposit: '{depositId}' has {confirmations} confirmations (required at least {_requiredBlockConfirmations}).");
            if (confirmations < _requiredBlockConfirmations)
            {
                return;
            }
            
            depositDetails.SetRefundClaimed(transactionHash);
            await _depositRepository.UpdateAsync(depositDetails);
            await _consumerNotifier.SendClaimedRefundAsync(depositId, depositDetails.DataAsset.Name, transactionHash);
            if (_logger.IsInfo) _logger.Info($"Claimed a refund for deposit: '{depositId}', transaction hash: '{transactionHash}'.");
        }

        private (long confirmations, bool blockFound) GetTransactionConfirmations(TxReceipt receipt)
        {
            var confirmations = 0;
            var block = _blockchainBridge.FindBlock(_blockchainBridge.Head.Hash);
            if (block is null)
            {
                return (0, false);
            }

            while (block.Number >= receipt.BlockNumber)
            {
                confirmations++;
                if (block.Hash == receipt.BlockHash)
                {
                    return (confirmations, true);
                }

                block = _blockchainBridge.FindBlock(block.ParentHash);
                if (block is null)
                {
                    return (confirmations, false);
                }
            }

            return (confirmations, false);
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

            if (_logger.IsWarn) _logger.Warn($"Active session for deposit: '{depositId}' was not found.");
            
            return null;
        }

        private bool IsDataAssetAvailable(DataAsset dataAsset)
            => dataAsset.State == DataAssetState.Published || dataAsset.State == DataAssetState.UnderMaintenance;

        private DataRequest CreateDataRequest(DepositDetails deposit)
        {
            var hash = Keccak.Compute(_nodePublicKey.Bytes);
            var signature = _wallet.Sign(hash, _consumerAddress);

            return new DataRequest(deposit.DataAsset.Id, deposit.Deposit.Units, deposit.Deposit.Value,
                deposit.Deposit.ExpiryTime, deposit.Pepper, deposit.DataAsset.Provider.Address, deposit.Consumer,
                signature);
        }

        private void SetActiveSession(ConsumerSession session)
        {
            _sessions.TryRemove(session.DepositId, out _);            
            _sessions.TryAdd(session.DepositId, session);
        }
    }
}