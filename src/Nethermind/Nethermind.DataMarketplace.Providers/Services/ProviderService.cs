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
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Validators;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Providers.Services
{
    internal class ProviderService : IProviderService
    {
        private readonly ConcurrentDictionary<string, INdmPlugin> _plugins =
            new ConcurrentDictionary<string, INdmPlugin>();

        private static readonly long Eth = (long) Unit.Ether;
        private static readonly int ConsumedUnitsDifferenceLimit = 100;

        private readonly AbiSignature _depositAbiSig = new AbiSignature("deposit",
            new AbiBytes(32),
            new AbiUInt(32),
            new AbiUInt(96),
            new AbiUInt(32),
            new AbiBytes(16),
            AbiType.Address,
            AbiType.Address);

        private readonly AbiSignature _earlyRefundTicketAbiSig = new AbiSignature("earlyRefundTicket",
            new AbiBytes(32), new AbiUInt(32));

        private readonly AbiSignature _dataAssetAbiSig = new AbiSignature("dataAsset",
            AbiType.Address, AbiType.String, AbiType.String, new AbiUInt(256),
            AbiType.String, AbiType.String, new AbiUInt(32), new AbiUInt(32),
            new AbiFixedLengthArray(new AbiUInt(256), 1), AbiType.String, new AbiUInt(32));

        private readonly ConcurrentDictionary<Keccak, Consumer> _consumers =
            new ConcurrentDictionary<Keccak, Consumer>();

        private readonly ConcurrentDictionary<Keccak, DataAsset> _dataAssets =
            new ConcurrentDictionary<Keccak, DataAsset>();

        private readonly IDataAssetRepository _dataAssetRepository;
        private readonly IProviderDepositApprovalRepository _depositApprovalRepository;
        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly IConsumerRepository _consumerRepository;
        private readonly IPaymentClaimRepository _paymentClaimRepository;
        private readonly IPaymentClaimProcessor _paymentClaimProcessor;
        private readonly IProviderSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;
        private readonly IEcdsa _ecdsa;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ILogger _logger;
        private readonly INdmDataPublisher _ndmDataPublisher;
        private readonly IGasPriceService _gasPriceService;
        private readonly IDataAvailabilityValidator _dataAvailabilityValidator;
        private readonly ISessionManager _sessionManager;
        private readonly ITransactionVerifier _transactionVerifier;
        private readonly IDepositManager _depositManager;
        private readonly IRefundPolicy _refundPolicy;
        private readonly IDepositService _depositService;
        private readonly IWallet _wallet;
        private readonly INdmBlockchainBridge _blockchainBridge;
        private Address _providerAddress;
        private Address _coldWalletAddress;
        private readonly PublicKey _nodeId;
        private readonly string _providerName;
        private readonly string _filesPath;
        private readonly double _fileMaxSize;
        private readonly uint _requiredBlockConfirmations;
        private readonly ulong _paymentGasLimit;
        private readonly ILogManager _logManager;
        private readonly bool _skipDepositVerification;
        private readonly bool _backgroundServicesDisabled;
        private readonly Timer? _timer;
        private bool _accountLocked;

        public ProviderService(IConfigManager configManager, string configId, IConsumerRepository consumerRepository,
            IDataAssetRepository dataAssetRepository, IProviderDepositApprovalRepository depositApprovalRepository,
            IPaymentClaimRepository paymentClaimRepository, IPaymentClaimProcessor paymentClaimProcessor,
            IProviderSessionRepository sessionRepository, ITimestamper timestamper, IEcdsa ecdsa,
            IAbiEncoder abiEncoder, INdmDataPublisher ndmDataPublisher, IGasPriceService gasPriceService,
            IDataAvailabilityValidator dataAvailabilityValidator, ISessionManager sessionManager,
            ITransactionVerifier transactionVerifier, IDepositManager depositManager, IRefundPolicy refundPolicy,
            IDepositService depositService, IWallet wallet, INdmBlockchainBridge blockchainBridge,
            Address providerAddress, Address coldWalletAddress, PublicKey nodeId, string providerName, string filesPath,
            double fileMaxSize, uint requiredBlockConfirmations, ulong paymentGasLimit, ILogManager logManager,
            bool skipDepositVerification = false, bool backgroundServicesDisabled = false)
        {
            _configManager = configManager;
            _configId = configId;
            _consumerRepository = consumerRepository;
            _dataAssetRepository = dataAssetRepository;
            _depositApprovalRepository = depositApprovalRepository;
            _paymentClaimRepository = paymentClaimRepository;
            _paymentClaimProcessor = paymentClaimProcessor;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
            _ecdsa = ecdsa;
            _abiEncoder = abiEncoder;
            _ndmDataPublisher = ndmDataPublisher ?? throw new ArgumentNullException(nameof(ndmDataPublisher));
            _gasPriceService = gasPriceService;
            _dataAvailabilityValidator = dataAvailabilityValidator;
            _sessionManager = sessionManager;
            _transactionVerifier = transactionVerifier;
            _depositManager = depositManager;
            _refundPolicy = refundPolicy;
            _depositService = depositService;
            _wallet = wallet;
            _blockchainBridge = blockchainBridge;
            _providerAddress = providerAddress ?? Address.Zero;
            _coldWalletAddress = coldWalletAddress ?? Address.Zero;
            _nodeId = nodeId;
            _providerName = providerName;
            _filesPath = filesPath;
            _fileMaxSize = fileMaxSize;
            _requiredBlockConfirmations = requiredBlockConfirmations;
            _paymentGasLimit = paymentGasLimit;
            _logManager = logManager;
            _skipDepositVerification = skipDepositVerification;
            _backgroundServicesDisabled = backgroundServicesDisabled;
            _logger = logManager.GetClassLogger();
            _dataAssetRepository.BrowseAsync(new GetDataAssets
            {
                Results = int.MaxValue
            }).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    if (_logger.IsError) _logger.Error("Error during loading data assets.", t.Exception);
                    return;
                }

                if (_logger.IsInfo) _logger.Info($"Loaded {t.Result.Items.Count} data assets.");
                foreach (DataAsset dataAsset in t.Result.Items)
                {
                    _dataAssets.TryAdd(dataAsset.Id, dataAsset);
                }
            });
            _wallet.AccountLocked += OnAccountLocked;
            _wallet.AccountUnlocked += OnAccountUnlocked;
            _accountLocked = !_wallet.IsUnlocked(_providerAddress);
            _ndmDataPublisher.DataPublished += async (s, e) => await SendDataAssetDataAsync(e.DataAssetData);
            if (_backgroundServicesDisabled)
            {
                return;
            }

            _timer = new Timer {Interval = 5000};
            _timer.Elapsed += OnTimeElapsed;
            _timer.Start();
        }

        private void OnAccountUnlocked(object? sender, AccountUnlockedEventArgs e)
        {
            if (e.Address != _providerAddress)
            {
                return;
            }

            _accountLocked = false;
            if (_logger.IsInfo) _logger.Info($"Unlocked provider account: '{e.Address}', connections can be accepted.");
        }

        private void OnAccountLocked(object? sender, AccountLockedEventArgs e)
        {
            if (e.Address != _providerAddress)
            {
                return;
            }

            _accountLocked = true;
            if (_logger.IsInfo) _logger.Info($"Locked provider account: '{e.Address}', all of the existing data streams will be disabled");

            foreach (ConsumerNode consumerNode in _sessionManager.GetConsumerNodes())
            {
                foreach (ProviderSession session in consumerNode.Sessions)
                {
                    foreach (SessionClient client in session.Clients)
                    {
                        session.DisableStream(client.Id);
                    }
                }
            }
        }

        private void OnTimeElapsed(object sender, ElapsedEventArgs e)
            => GetConsumersAsync(new GetConsumers
                {
                    Results = int.MaxValue
                })
                .ContinueWith(async t =>
                {
                    var paymentClaimsResult = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
                    {
                        OnlyUnclaimed = true,
                        Page = 1,
                        Results = int.MaxValue
                    });

                    if (paymentClaimsResult.IsEmpty)
                    {
                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Found {paymentClaimsResult.TotalResults} payment claim(s) without updated transaction costs.");

                    foreach (PaymentClaim paymentClaim in paymentClaimsResult.Items)
                    {
                        if (paymentClaim.Status == PaymentClaimStatus.Unknown)
                        {
                            if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim.Id}) for deposit: '{paymentClaim.DepositId}' is missing a transaction hash, sending a transaction once again...");
                            UInt256 gasPrice = await _gasPriceService.GetCurrentPaymentClaimGasPriceAsync();
                            Keccak? transactionHash = await _paymentClaimProcessor.SendTransactionAsync(paymentClaim, gasPrice);
                            if (transactionHash is null)
                            {
                                if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim.Id}) for deposit: '{paymentClaim.DepositId}' did not receive a transaction hash.");
                                continue;
                            }

                            if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim.Id}) for deposit: '{paymentClaim.DepositId}' received a transaction hash: '{transactionHash}'.");
                            paymentClaim.AddTransaction(TransactionInfo.Default(transactionHash, 0, gasPrice,
                                _paymentGasLimit, _timestamper.UnixTime.Seconds));
                            paymentClaim.SetStatus(PaymentClaimStatus.Sent);
                            await _paymentClaimRepository.UpdateAsync(paymentClaim);
                            continue;
                        }

                        NdmTransaction? transactionDetails = null;
                        TransactionInfo includedTransaction = paymentClaim.Transactions.SingleOrDefault(tx => tx.State == TransactionState.Included);
                        var pendingTransactions = paymentClaim.Transactions
                            .Where(tx => tx.State == TransactionState.Pending)
                            .OrderBy(tx => tx.Timestamp);

                        if (_logger.IsInfo) _logger.Info($"Payment claim: '{paymentClaim.Id}' pending transactions: {string.Join(", ", pendingTransactions.Select(t => $"{t.Hash} [{t.Type}]"))}");

                        if (includedTransaction is null)
                        {
                            foreach (TransactionInfo pendingTransaction in pendingTransactions)
                            {
                                Keccak? transactionHash = pendingTransaction.Hash;
                                if (transactionHash == null)
                                {
                                    if (_logger.IsWarn) _logger.Warn($"Pending payment claim transaction is missing for payment claim: '{paymentClaim.Id}'.");
                                    continue;
                                }

                                transactionDetails = await _blockchainBridge.GetTransactionAsync(transactionHash);
                                if (transactionDetails is null)
                                {
                                    if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{transactionHash}' for payment claim: '{paymentClaim.Id}' to be confirmed.");
                                    continue;
                                }

                                if (transactionDetails.IsPending)
                                {
                                    if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}' for payment claim: '{paymentClaim.Id}' is still pending.");
                                    continue;
                                }

                                paymentClaim.SetIncludedTransaction(transactionHash);
                                if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}', type: '{pendingTransaction.Type}' for payment claim: '{paymentClaim.Id}' was included into block: {transactionDetails.BlockNumber}.");
                                await _paymentClaimRepository.UpdateAsync(paymentClaim);
                                includedTransaction = pendingTransaction;
                                break;
                            }
                        }
                        else if (includedTransaction.Type == TransactionType.Cancellation)
                        {
                            return;
                        }
                        else
                        {
                            transactionDetails = includedTransaction.Hash == null ? null : await _blockchainBridge.GetTransactionAsync(includedTransaction.Hash);
                            if (transactionDetails is null)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Transaction (set as included) was not found for hash: '{includedTransaction.Hash}' for paymen claim: '{paymentClaim.Id}'.");
                                return;
                            }
                        }

                        if (includedTransaction is null)
                        {
                            return;
                        }

                        TransactionInfo? currentTransaction = paymentClaim!.Transaction;
                        if (currentTransaction == null || currentTransaction.Hash == null)
                        {
                            continue;
                        }

                        if (transactionDetails == null)
                        {
                            continue;
                        }
                        
                        if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim!.Id}) for deposit: '{paymentClaim!.DepositId}', transaction hash: '{currentTransaction.Hash}', gas price: {currentTransaction.GasPrice} wei, gas used: {transactionDetails.GasUsed}.");

                        TransactionVerifierResult verifierResult = await _transactionVerifier.VerifyAsync(transactionDetails);

                        if (!verifierResult.BlockFound)
                        {
                            paymentClaim!.Reject();
                            await _paymentClaimRepository.UpdateAsync(paymentClaim!);
                            if (_logger.IsWarn) _logger.Warn($"Payment claim (id: {paymentClaim!.Id}) for deposit: '{paymentClaim!.DepositId}' has been rejected - block number: {transactionDetails.BlockNumber}, hash: '{transactionDetails.BlockHash}') for transaction: '{currentTransaction.Hash}' was not found.");
                            continue;
                        }

                        if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim!.Id}) for deposit: '{paymentClaim!.DepositId}' has {verifierResult.Confirmations} confirmations (required at least {verifierResult.RequiredConfirmations}) for transaction hash: '{currentTransaction.Hash}'.");
                        if (!verifierResult.Confirmed)
                        {
                            continue;
                        }

                        BigInteger? transactionCost = (BigInteger) currentTransaction.GasPrice * transactionDetails.GasUsed;
                        if (transactionCost != null)
                        {
                            string ethCost = ((decimal) transactionCost / Eth).ToString("0.0000");
                            paymentClaim!.SetTransactionCost((UInt256) transactionCost);
                            await _paymentClaimRepository.UpdateAsync(paymentClaim!);
                            if (_logger.IsInfo) _logger.Info($"Updated transaction cost: {transactionCost} wei ({ethCost} ETH) for payment claim : '{paymentClaim!.Id}' for deposit: '{paymentClaim!.DepositId}', transaction hash: '{currentTransaction.Hash}'.");
                        }
                    }
                });

        public event EventHandler<AddressChangedEventArgs>? AddressChanged;
        public event EventHandler<AddressChangedEventArgs>? ColdWalletAddressChanged;
        public Address GetAddress() => _providerAddress;

        public Address GetColdWalletAddress() => _coldWalletAddress;

        public async Task ChangeAddressAsync(Address address)
        {
            if (_providerAddress == address)
            {
                return;
            }

            Address oldAddress = _providerAddress;
            if (_logger.IsInfo) _logger.Info($"Changing provider address: '{oldAddress}' -> '{address}'...");
            _providerAddress = address;
            _accountLocked = !_wallet.IsUnlocked(_providerAddress);
            AddressChanged?.Invoke(this, new AddressChangedEventArgs(oldAddress, _providerAddress));
            _depositManager.ChangeAddress(_providerAddress);
            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Changing provider address: '{oldAddress}' -> '{address}' failed due to missing config with ID {_configId}");

                return;
            }

            config.ProviderAddress = _providerAddress.ToString();
            await _configManager.UpdateAsync(config);
            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes())
            {
                node.Peer.ChangeHostProviderAddress(_providerAddress);
                node.Peer.SendProviderAddressChanged(_providerAddress);
                await FinishSessionsAsync(node.Peer, false);
            }

            if (_logger.IsInfo) _logger.Info($"Changed provider address: '{_providerAddress}' -> '{address}'.");
        }

        public async Task ChangeColdWalletAddressAsync(Address address)
        {
            if (_coldWalletAddress == address)
            {
                return;
            }

            Address oldAddress = _coldWalletAddress;
            if (_logger.IsInfo) _logger.Info($"Changing provider cold wallet address: '{oldAddress}' -> '{address}'...");
            _coldWalletAddress = address;
            ColdWalletAddressChanged?.Invoke(this, new AddressChangedEventArgs(oldAddress, _coldWalletAddress));
            _depositManager.ChangeColdWalletAddress(_coldWalletAddress);
            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                throw new InvalidDataException($"Cannot change cold wallet address on missing config (ID: {_configId})");
            }

            config.ProviderColdWalletAddress = _coldWalletAddress.ToString();
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Changed provider cold wallet address: '{_coldWalletAddress}' -> '{address}'.");
        }

        public Task ChangeConsumerAddressAsync(INdmProviderPeer peer, Address address)
        {
            if (peer.ConsumerAddress == address)
            {
                return Task.CompletedTask;
            }

            if (_logger.IsInfo) _logger.Info($"Changing consumer address: '{peer.ConsumerAddress}' -> '{address}' for peer: '{peer.NodeId}'.");

            var nodes = _sessionManager.GetConsumerNodes().Where(n => n.Peer.ConsumerAddress == peer.ConsumerAddress);
            foreach (ConsumerNode node in nodes)
            {
                node.Peer.ChangeConsumerAddress(address);
                foreach (ProviderSession session in node.Sessions)
                {
                    foreach (SessionClient client in session.Clients)
                    {
                        session.DisableStream(client.Id);
                        node.Peer.SendDataStreamDisabled(session.DepositId, client.Id);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public async Task<Consumer?> GetConsumerAsync(Keccak depositId)
        {
            Consumer? consumer = await _consumerRepository.GetAsync(depositId);
            if (consumer is null)
            {
                return null;
            }

            uint now = (uint) _timestamper.UnixTime.Seconds;
            uint consumedUnits = 0;
            if (consumer.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                consumedUnits = now - consumer.VerificationTimestamp;
                if (consumer.DataRequest.Units < consumedUnits)
                {
                    consumedUnits = consumer.DataRequest.Units;
                }
            } 
            else 
            {
                var sessions = await _sessionRepository.BrowseAsync(new GetProviderSessions
                {
                    DepositId = consumer.DepositId,
                    Results = int.MaxValue
                });
                consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            };
            consumer.SetConsumedUnits(consumedUnits);

            return consumer;
        }

        public async Task<PagedResult<Consumer>> GetConsumersAsync(GetConsumers query)
        {
            var consumers = await _consumerRepository.BrowseAsync(query);
            foreach (Consumer consumer in consumers.Items)
            {
                uint consumedUnits = 0;
                uint now = (uint) _timestamper.UnixTime.Seconds;
                if (consumer.DataAsset.UnitType == DataAssetUnitType.Time)
                {
                    consumedUnits = now - consumer.VerificationTimestamp;
                    if (consumer.DataRequest.Units < consumedUnits)
                    {
                        consumedUnits = consumer.DataRequest.Units;
                    }
                } 
                else 
                {
                    var sessions = await _sessionRepository.BrowseAsync(new GetProviderSessions
                    {
                        DepositId = consumer.DepositId,
                        Results = int.MaxValue
                    });
                    consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
                };
                consumer.SetConsumedUnits(consumedUnits);
            }

            return consumers;
        }

        public Task<PagedResult<DataAsset>> GetDataAssetsAsync(GetDataAssets query)
            => _dataAssetRepository.BrowseAsync(query);

        public async Task<Keccak?> AddDataAssetAsync(string name, string description, UInt256 unitPrice,
            DataAssetUnitType unitType, uint minUnits, uint maxUnits, DataAssetRules rules, string? file = null,
            byte[]? data = null, QueryType? queryType = null, string? termsAndConditions = null, bool kycRequired = false,
            string? plugin = null)
        {
            if (_providerAddress is null || _providerAddress == Address.Zero)
            {
                if (_logger.IsError) _logger.Error($"Cannot add the data asset - provider address is not set.");
                return null;
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            uint chainId = (uint) await _blockchainBridge.GetNetworkIdAsync();

            if(string.IsNullOrEmpty(config.ContractAddress))
            {
                if(_logger.IsError) _logger.Error("Contract address is not set in ndm config. Could not add data asset.");
                return null;
            }

            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _dataAssetAbiSig,
                _providerAddress, name, description, unitPrice,
                unitType.ToString(), (queryType ?? QueryType.Stream).ToString(), minUnits, maxUnits, new[] {rules.Expiry.Value}, config.ContractAddress, chainId);

            Keccak id = Keccak.Compute(abiHash);
            if (await _dataAssetRepository.ExistsAsync(id))
            {
                if (_logger.IsError) _logger.Error($"Data asset with id: '{id}' already exists.");
                return null;
            }

            HandleFile(id, file, data);
            DataAsset dataAsset = new DataAsset(id, name, description, unitPrice, unitType, minUnits, maxUnits,
                rules, new DataAssetProvider(_providerAddress, _providerName), file, queryType ?? QueryType.Stream,
                DataAssetState.Unpublished, termsAndConditions, kycRequired, plugin);
            await _dataAssetRepository.AddAsync(dataAsset);
            _dataAssets.TryAdd(dataAsset.Id, dataAsset);
            if (_logger.IsInfo) _logger.Info($"Data asset added: '{id}'.");

            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes())
            {
                node.Peer.SendDataAsset(dataAsset);
            }

            return id;
        }

        private void HandleFile(Keccak id, string? file, byte[]? data)
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                ValidateFile(file);
            }

            if (data is null || data.Length == 0)
            {
                return;
            }

            ValidateFile(data);
            string filename = id.ToString();
            if (!Directory.Exists(_filesPath))
            {
                if (_logger.IsInfo) _logger.Info($"Creating directory: '{_filesPath}'.");
                Directory.CreateDirectory(_filesPath);
            }

            using (FileStream stream = File.Create(GetPath(filename)))
            {
                if (_logger.IsInfo) _logger.Info($"Saving file: '{filename}'.");
                stream.Write(data);
            }
        }

        public async Task<bool> RemoveDataAssetAsync(Keccak id)
        {
            _dataAssets.TryRemove(id, out _);
            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(id);
            if (dataAsset is null)
            {
                throw new ArgumentException($"Data asset with id: '{id}' was not found.");
            }

            if (dataAsset.State == DataAssetState.Archived)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{id}' was already archived.");

                return false;
            }

            if (!string.IsNullOrWhiteSpace(dataAsset.File))
            {
                string path = GetPath(dataAsset.File);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            var consumers = await _consumerRepository.BrowseAsync(new GetConsumers
            {
                AssetId = id,
                Results = int.MaxValue
            });
            if (consumers.IsEmpty)
            {
                await _dataAssetRepository.RemoveAsync(id);
                _dataAssets.TryRemove(id, out _);
                if (_logger.IsInfo) _logger.Info($"Data asset removed: '{id}'.");
            }
            else
            {
                dataAsset.SetState(DataAssetState.Archived);
                await _dataAssetRepository.UpdateAsync(dataAsset);
                if (_logger.IsInfo) _logger.Info($"Data asset archived: '{id}'.");
                foreach (Consumer consumer in consumers.Items)
                {
                    foreach (ConsumerNode node in _sessionManager.GetConsumerNodes(consumer.DepositId))
                    {
                        SendEarlyRefundTicket(consumer.DepositId, RefundReason.DataAssetStateChanged, node.Peer);
                    }
                }
            }

            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes())
            {
                node.Peer.SendDataAssetRemoved(dataAsset.Id);
            }

            return true;
        }

        public void AddConsumerPeer(INdmProviderPeer peer) => _sessionManager.AddPeer(peer);

        private void ValidateFile(string file)
        {
            if (_logger.IsInfo) _logger.Info($"Validating file: '{file}'.");
            string path = GetPath(file);
            if (!File.Exists(path))
            {
                return;
            }

            ValidateFile(File.ReadAllBytes(path));
        }

        private void ValidateFile(byte[] data)
        {
            if (data is null)
            {
                return;
            }

            if (data.Length > _fileMaxSize)
            {
                throw new ArgumentException($"Maximum file size for a data asset is {_fileMaxSize} bytes. " +
                                            $"Provided file has: {data.Length} bytes.", nameof(data));
            }
        }

        private string GetPath(string file) => $"{_filesPath}/{file}";

        public async Task StartSessionAsync(DataRequest dataRequest, uint consumedUnitsFromConsumer, INdmProviderPeer peer)
        {
            (bool canStart, Keccak depositId, uint verificationTimestamp) = await ValidateDataRequestAsync(dataRequest, peer);
            if (!canStart)
            {
                return;
            }

            bool isNewSession = await CreateSessionIfDoesNotExistAsync(depositId, dataRequest, verificationTimestamp,
                consumedUnitsFromConsumer, peer);
            if (!isNewSession)
            {
                return;
            }

            if (!await ValidateConsumedUnitsAsync(depositId, peer, consumedUnitsFromConsumer))
            {
                return;
            }

            await HandleUnpaidUnitsAsync(depositId, peer);
        }

        private async Task<(bool canStart, Keccak depositId, uint verificationTimestamp)> ValidateDataRequestAsync(
            DataRequest dataRequest, INdmProviderPeer ndmPeer)
        {
            Keccak depositId = GetDepositId(dataRequest);
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_providerAddress}' is locked, can't start a session.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.ProviderUnavailable);
                return (false, depositId, 0);
            }

            Keccak hash = Keccak.Compute(ndmPeer.NodeId.Bytes);
            Address address = _ecdsa.RecoverPublicKey(dataRequest.Signature, hash).Address;
            if (!dataRequest.Consumer.Equals(address))
            {
                if (_logger.IsInfo) _logger.Info($"Invalid signature for consumer: '{dataRequest.Consumer}' <> '{address}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.InvalidSignature);

                return (false, depositId, 0);
            }

            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(dataRequest.DataAssetId);
            if (dataAsset is null)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{dataRequest.DataAssetId}' was not found for consumer: '{dataRequest.Consumer}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.DataAssetNotFound);
                SendEarlyRefundTicket(depositId, RefundReason.InvalidDataAsset, ndmPeer);

                return (false, depositId, 0);
            }

            if (dataAsset.State == DataAssetState.Closed || dataAsset.State == DataAssetState.Archived)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{dataRequest.DataAssetId}' is closed, state: '{dataAsset.State}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.DataAssetClosed);
                SendEarlyRefundTicket(depositId, RefundReason.DataAssetStateChanged, ndmPeer);

                return (false, depositId, 0);
            }

            if (!(await VerifyKycAsync(dataAsset, dataRequest.Consumer)))
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{dataRequest.DataAssetId}' KYC was not confirmed for consumer: '{dataRequest.Consumer}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.KycUnconfirmed);
                SendEarlyRefundTicket(depositId, RefundReason.UnconfirmedKyc, ndmPeer);

                return (false, depositId, 0);
            }

            if (dataAsset.MinUnits > dataRequest.Units || dataAsset.MaxUnits < dataRequest.Units)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid data request units: '{dataRequest.Units}', min: '{dataAsset.MinUnits}', max: '{dataAsset.MaxUnits}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.InvalidDataRequestUnits);
                SendEarlyRefundTicket(depositId, RefundReason.InvalidDataRequestUnits, ndmPeer);

                return (false, depositId, 0);
            }

            UInt256 unitsValue = dataRequest.Units * dataAsset.UnitPrice;
            if (dataRequest.Units * dataAsset.UnitPrice != dataRequest.Value)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid data request value: '{dataRequest.Value}', while it should be: '{unitsValue}'.");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.InvalidDataRequestValue);
                SendEarlyRefundTicket(depositId, RefundReason.InvalidDataRequestValue, ndmPeer);

                return (false, depositId, 0);
            }

            ulong now = _timestamper.UnixTime.Seconds;
            if (_timestamper.UnixTime.Seconds >= dataRequest.ExpiryTime)
            {
                if (_logger.IsInfo) _logger.Info($"Data request for deposit: {depositId} is expired ({now} >= {dataRequest.ExpiryTime}).");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.DepositExpired);

                return (false, depositId, 0);
            }

            uint verificationTimestamp = await VerifyDepositAsync(depositId, dataRequest.Consumer);
            if (verificationTimestamp <= 0)
            {
                if (_logger.IsInfo) _logger.Info($"Data request for deposit: {depositId} has not been verified, timestamp: {verificationTimestamp}).");
                ndmPeer.SendDataRequestResult(depositId, DataRequestResult.DepositUnverified);

                return (false, depositId, 0);
            }

            ndmPeer.SendDataRequestResult(depositId, DataRequestResult.DepositVerified);

            return (true, depositId, verificationTimestamp);
        }

        private async Task<bool> VerifyKycAsync(DataAsset dataAsset, Address consumer)
        {
            if (!dataAsset.KycRequired)
            {
                return true;
            }

            Keccak assetId = dataAsset.Id;
            Keccak id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
            DepositApproval? depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was not found for consumer: '{consumer}'.");

                return false;
            }

            if (depositApproval.State != DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' for consumer: '{consumer}' has state: '{depositApproval.State}'.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was confirmed for consumer: '{consumer}', required KYC is valid.");

            return true;
        }

        private async Task<bool> CreateSessionIfDoesNotExistAsync(Keccak depositId, DataRequest dataRequest,
            uint verificationTimestamp, uint consumedUnitsFromConsumer, INdmProviderPeer ndmPeer)
        {
            Consumer? consumer = await _consumerRepository.GetAsync(depositId);
            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(dataRequest.DataAssetId);
            if (dataAsset is null)
            {
                if (_logger.IsInfo) _logger.Info($"Could not find the data asset {dataAsset} in the repository.");
                return false;
            }

            uint upfrontUnits = (uint) (dataAsset.Rules.UpfrontPayment?.Value ?? 0);
            if (consumer is null)
            {
                if (_logger.IsInfo) _logger.Info($"Added a new consumer for deposit: '{depositId}', address: '{ndmPeer.ConsumerAddress}', timestamp: {verificationTimestamp}.");
                consumer = new Consumer(depositId, verificationTimestamp, dataRequest, dataAsset);
                await _consumerRepository.AddAsync(consumer);
            }

            _consumers.TryAdd(consumer.DepositId, consumer);
            ProviderSession? session = _sessionManager.GetSession(depositId, ndmPeer);
            if (!(session is null))
            {
                if (_logger.IsInfo) _logger.Info($"Already an active session with id: '{session.Id}' for deposit: '{depositId}', node: '{ndmPeer.NodeId}', consumer: '{ndmPeer.ConsumerAddress}'.");
                ndmPeer.SendSessionStarted(session);

                return false;
            }

            ulong now = _timestamper.UnixTime.Seconds;
            Keccak sessionId = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(ndmPeer.NodeId.Bytes),
                Rlp.Encode(_nodeId.Bytes), Rlp.Encode(now)).Bytes);
            var nodeSessions = await _sessionRepository.BrowseAsync(new GetProviderSessions
            {
                DepositId = consumer.DepositId,
                ConsumerAddress = ndmPeer.ConsumerAddress,
                Results = int.MaxValue
            });
            uint nodeConsumedUnits = (uint) nodeSessions.Items.Sum(s => s.ConsumedUnits);
            if (_logger.IsInfo) _logger.Info($"Node '{ndmPeer.NodeId}' has consumed: {nodeConsumedUnits} units (declared {consumedUnitsFromConsumer}) for deposit: '{depositId}'.");

            if (ndmPeer.ConsumerAddress == null)
            {
                throw new InvalidOperationException($"Not possible to open session for a consumer with node ID {ndmPeer.NodeId} and without address set");
            }

            session = new ProviderSession(sessionId, depositId, dataAsset.Id, ndmPeer.ConsumerAddress, ndmPeer.NodeId,
                _providerAddress, _nodeId, nodeConsumedUnits, consumedUnitsFromConsumer);
            session.Start(now);
            if (upfrontUnits > 0 && !nodeSessions.Items.Any())
            {
                session.AddUnpaidUnits(upfrontUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {upfrontUnits} for deposit: '{depositId}' based on upfront payment.");
            }

            await _sessionRepository.AddAsync(session);
            _sessionManager.SetSession(session, ndmPeer);
            ndmPeer.SendSessionStarted(session);
            if (_logger.IsInfo) _logger.Info($"Started a session with id: '{session.Id}' for deposit: '{depositId}', node: '{ndmPeer.NodeId}', consumer: '{ndmPeer.ConsumerAddress}'.");
            await _depositManager.InitAsync(depositId, session.UnpaidUnits);

            return true;
        }

        private async Task<bool> ValidateConsumedUnitsAsync(Keccak depositId, INdmProviderPeer ndmPeer,
            long consumedUnitsFromConsumer)
        {
            ProviderSession? session = _sessionManager.GetSession(depositId, ndmPeer);
            if (session is null)
            {
                throw new InvalidDataException($"Could not resolve session fro deposit with ID {depositId}");
            }

            if (session.StartUnitsFromProvider <= session.StartUnitsFromConsumer)
            {
                return true;
            }

            uint unitsDifference = session.StartUnitsFromProvider - session.StartUnitsFromConsumer;
            if (unitsDifference > ConsumedUnitsDifferenceLimit)
            {
                if (_logger.IsInfo) _logger.Info($"Invalid consumed units difference ({unitsDifference} > {ConsumedUnitsDifferenceLimit}) for deposit: '{depositId}', provider units: {session.ConsumedUnits}, consumer units: {consumedUnitsFromConsumer}.");
                SendEarlyRefundTicket(depositId, RefundReason.InvalidConsumedUnitsAmount, ndmPeer);

                return false;
            }

            if (session.ConsumedUnits < unitsDifference)
            {
                return true;
            }

            uint unpaid = session.UnpaidUnits > unitsDifference ? session.UnpaidUnits - unitsDifference : 0;
            session.SetConsumedUnits(session.ConsumedUnits - unitsDifference);
            session.SetUnpaidUnits(unpaid);
            await _sessionRepository.UpdateAsync(session);
            if (_logger.IsInfo) _logger.Info($"Consumed units difference for deposit: '{depositId}', {unitsDifference} units. Lowering consumed & unpaid units amount.");

            return true;
        }

        private async Task HandleUnpaidUnitsAsync(Keccak depositId, INdmProviderPeer ndmPeer)
        {
            if (!_consumers.TryGetValue(depositId, out Consumer? consumer))
            {
                if (_logger.IsInfo) _logger.Info($"Consumer was not found for deposit: '{depositId}'.");
                return;
            }

            ulong now = _timestamper.UnixTime.Seconds;
            ProviderSession? currentSession = _sessionManager.GetSession(depositId, ndmPeer);
            if (currentSession is null)
            {
                if (_logger.IsError) _logger.Error($"Session for deposit {depositId} missing");
                throw new InvalidDataException($"Session for deposit {depositId} missing");
            }

            if (consumer!.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                uint sessionUnpaidUnits = (uint) (now - currentSession.StartTimestamp - currentSession.PaidUnits);
                currentSession.AddUnpaidUnits(sessionUnpaidUnits);
                await _sessionRepository.UpdateAsync(currentSession);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: '{sessionUnpaidUnits}' for deposit: '{depositId}' based on time.");
            }

            await _depositManager.HandleUnpaidUnitsAsync(depositId, ndmPeer);
        }

        public Task FinishSessionAsync(Keccak depositId, INdmProviderPeer peer, bool removePeer = true)
            => _sessionManager.FinishSessionAsync(depositId, peer, removePeer);

        public Task FinishSessionsAsync(INdmProviderPeer peer, bool removePeer = true)
            => _sessionManager.FinishSessionsAsync(peer, removePeer);

        private async Task HandleConsumerDataRequestAsync(INdmProviderPeer peer, Consumer consumer, DataAsset dataAsset,
            string client, string[] args)
        {
            bool hasArgs = args.Length > 0;
            bool hasIterations = uint.TryParse(hasArgs ? args[0] : "1", out uint iterations);
            if (!hasIterations)
            {
                iterations = 1;
            }

            if (_logger.IsInfo) _logger.Info($"Handling the query for deposit: '{consumer.DepositId}'.");
            uint consumedUnits = _depositManager.GetConsumedUnits(consumer.DepositId);
            if (consumedUnits + iterations > consumer.DataRequest.Units)
            {
                uint requestedIterations = iterations;
                iterations = consumer.DataRequest.Units - consumedUnits;
                if (_logger.IsInfo) _logger.Info($"Lowering number of iterations from {requestedIterations} to {iterations}.");
            }

            if (iterations == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"No iterations left to send a query for deposit: '{consumer.DepositId}'.");
                SendInvalidData(consumer.DepositId, InvalidDataReason.NoUnitsLeft);
                return;
            }

            string? pluginName = dataAsset.Plugin?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(pluginName))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAsset.Id}' has no plugin assigned.");
                SendInvalidData(consumer.DepositId, InvalidDataReason.PluginNotFound);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Data asset: '{dataAsset.Id}' uses the plugin: '{pluginName}'.");
            if (!_plugins.TryGetValue(pluginName, out INdmPlugin? plugin))
            {
                if (_logger.IsWarn) _logger.Warn($"Plugin: '{pluginName}' was not found (required by data asset: '{dataAsset.Id}').");
                ShowPluginsPool();
                SendInvalidData(consumer.DepositId, InvalidDataReason.PluginNotFound);
                return;
            }

            Metrics.ProviderReceivedQueries++;
            if (_logger.IsInfo) _logger.Info($"Found a plugin: '{pluginName}' [type: '{plugin.Type}'] to handle the query for deposit: '{consumer.DepositId}'.");
            IEnumerable<string> dataArgs = hasArgs ? hasIterations ? args.Skip(1) : args : Enumerable.Empty<string>();
            switch (dataAsset.QueryType)
            {
                case QueryType.Document:
                {
                    await HandleDocumentAsync(plugin, consumer, client, dataArgs);
                    return;
                }
                case QueryType.Query:
                {
                    await HandleQueryAsync(plugin, consumer, client, dataArgs);
                    return;
                }
                case QueryType.Stream:
                {
                    await HandleStreamAsync(plugin, peer, consumer, client, dataArgs);
                    return;
                }
                default:
                {
                    if (_logger.IsError) _logger.Error($"Not supported data asset type: {dataAsset.QueryType}.");
                    SendInvalidData(consumer.DepositId, InvalidDataReason.InternalError);
                    return;
                }
            }
        }

        private Task HandleDocumentAsync(INdmPlugin plugin, Consumer consumer, string client, IEnumerable<string> args)
        {
            Keccak depositId = consumer.DepositId;
            if (_logger.IsError) _logger.Error("Document data asset type is not supported yet.");
            SendInvalidData(depositId, InvalidDataReason.InternalError);
            return Task.CompletedTask;
        }

        private async Task HandleQueryAsync(INdmPlugin plugin, Consumer consumer, string client,
            IEnumerable<string> args)
        {
            Keccak depositId = consumer.DepositId;
            try
            {
                if (_logger.IsInfo) _logger.Info($"Sending the query for deposit: '{depositId}'.");
                string? result = await plugin.QueryAsync(args);
                bool successful = !string.IsNullOrWhiteSpace(result);
                if (!successful)
                {
                    Metrics.ProviderInvalidQueries++;
                    if (_logger.IsWarn) _logger.Info($"Received the invalid query result for deposit: '{depositId}'.");
                    SendInvalidData(depositId, InvalidDataReason.InvalidResult);
                    return;
                }

                Metrics.ProviderSuccessfulQueries++;
                if (_logger.IsInfo) _logger.Info($"Received the successful query result for deposit: '{depositId}'.");
                await TrySendDataAssetDataAsync(new DataAssetData(consumer.DataAsset.Id, result), consumer, client);
            }
            catch (Exception ex)
            {
                Metrics.ProviderFailedQueries++;
                if (_logger.IsError) _logger.Error("Error during processing the query.", ex);
                SendInvalidData(depositId, InvalidDataReason.InternalError);
            }
        }

        private async Task HandleStreamAsync(INdmPlugin plugin, INdmProviderPeer peer, Consumer consumer, string client,
            IEnumerable<string> args)
        {
            Keccak depositId = consumer.DepositId;
            try
            {
                if (_logger.IsInfo) _logger.Info($"Data will be streamed for deposit: '{depositId}'.");
                uint unitsBeforeStream = _depositManager.GetConsumedUnits(depositId);
                ProviderSession? session = _sessionManager.GetSession(depositId, peer);
                SessionClient? sessionClient = session?.GetClient(client);
                if (sessionClient is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Session client: '{client}' was not found for deposit: '{consumer.DepositId}' - data will not be streamed.");
                    SendInvalidData(depositId, InvalidDataReason.SessionClientNotFound);
                    return;
                }

                await plugin.SubscribeAsync(async data => await TrySendDataAssetDataAsync(
                        new DataAssetData(consumer.DataAsset.Id, data), consumer, client),
                    () => sessionClient.StreamEnabled && _depositManager.HasAvailableUnits(depositId), args,
                    sessionClient.CancellationToken);

                uint unitsAfterStream = _depositManager.GetConsumedUnits(depositId) - unitsBeforeStream;
                if (_logger.IsInfo) _logger.Info($"Data stream has completed for deposit: '{depositId}', client: '{client}'.");
                if (unitsAfterStream > 0 || !sessionClient.StreamEnabled)
                {
                    return;
                }

                if (_logger.IsWarn) _logger.Warn($"No units have been consumed during the data stream for deposit: '{depositId}', probably due to the invalid parameters.");
                SendInvalidData(depositId, InvalidDataReason.InvalidResult);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error during processing the stream.", ex);
                SendInvalidData(depositId, InvalidDataReason.InternalError);
            }
        }

        public async Task<bool> DisableDataStreamAsync(Keccak depositId, string client, INdmProviderPeer peer)
        {
            if (!_consumers.TryGetValue(depositId, out Consumer? consumer))
            {
                if (_logger.IsWarn) _logger.Warn($"Consumer was not found for deposit: '{depositId}'.");
                return false;
            }

            ProviderSession? session = _sessionManager.GetSession(depositId, peer);
            if (session is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Session for deposit: '{depositId}', node: '{peer.NodeId}' was not found.");
                return false;
            }

            Keccak dataAssetId = consumer.DataAsset.Id;
            if (!_dataAssets.TryGetValue(consumer.DataAsset.Id, out DataAsset? dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset with id: '{dataAssetId}' was not found for deposit: '{consumer.DepositId}'.");
                SendInvalidData(consumer.DepositId, InvalidDataReason.DataAssetNotFound);
                return false;
            }

            if (dataAsset.QueryType != QueryType.Stream)
            {
                return true;
            }

            if (!session.IsDataStreamEnabled(client))
            {
                if (_logger.IsWarn) _logger.Warn($"Session for deposit: '{depositId}', node: '{peer.NodeId}' is not enabled.");
                return false;
            }

            session.DisableStream(client);
            peer.SendDataStreamDisabled(depositId, client);
            await _sessionRepository.UpdateAsync(session);
            if (_logger.IsInfo) _logger.Info($"Disabled data stream for consumer: '{peer.ConsumerAddress}' [node: '{peer.NodeId}'], deposit: '{depositId}', client: '{client}'.");

            return true;
        }

        public async Task<bool> EnableDataStreamAsync(Keccak depositId, string client, string[] args, INdmProviderPeer peer)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_providerAddress}' is locked, can't start a session.");
                return false;
            }

            if (!_consumers.TryGetValue(depositId, out Consumer? consumer))
            {
                if (_logger.IsInfo) _logger.Info($"Consumer was not found for deposit: '{depositId}'.");
                return false;
            }

            ProviderSession? session = _sessionManager.GetSession(depositId, peer);
            if (session is null)
            {
                if (_logger.IsInfo) _logger.Info($"Session for deposit: '{depositId}', node: '{peer.NodeId}' was not found.");
                return false;
            }

            if (session.DataAvailability != DataAvailability.Available)
            {
                if (_logger.IsInfo) _logger.Info($"Cannot enable stream, reason: '{session.DataAvailability}'");
                return false;
            }

            Keccak dataAssetId = consumer.DataAsset.Id;
            if (!_dataAssets.TryGetValue(consumer.DataAsset.Id, out DataAsset? dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset with id: '{dataAssetId}' was not found for deposit: '{consumer.DepositId}'.");
                SendInvalidData(consumer.DepositId, InvalidDataReason.DataAssetNotFound);
                return false;
            }

            if (dataAsset.QueryType == QueryType.Stream)
            {
                if (session.IsDataStreamEnabled(client))
                {
                    if (_logger.IsInfo) _logger.Info($"Disabling the current data stream for '{consumer.DataRequest.Consumer}' [node: '{peer.NodeId}'], deposit: '{depositId}', client: '{client}' before enabling a new one.");
                    await DisableDataStreamAsync(depositId, client, peer);
                }

                session.EnableStream(client, args);
                peer.SendDataStreamEnabled(depositId, client, args);
                await _sessionRepository.UpdateAsync(session);
                if (_logger.IsInfo) _logger.Info($"Enabled data stream for consumer: '{consumer.DataRequest.Consumer}' [node: '{peer.NodeId}'], deposit: '{depositId}', client: '{client}', args: {string.Join(", ", args)}.");
            }
            else if (dataAsset.QueryType == QueryType.Query)
            {
                if (_logger.IsInfo) _logger.Info($"Processing the query for node: '{consumer.DataRequest.Consumer}' [node: '{peer.NodeId}'], deposit: '{depositId}', client: '{client}', args: {string.Join(", ", args)}.");
            }

            await HandleConsumerDataRequestAsync(peer, consumer, dataAsset, client, args);

            return true;
        }

        public async Task SendDataAssetDataAsync(DataAssetData dataAssetData)
        {
            if (!_dataAssets.ContainsKey(dataAssetData.AssetId))
            {
                if (_logger.IsTrace) _logger.Trace($"No data asset with id: '{dataAssetData.AssetId}' has been found.");
                return;
            }

            var consumers = _consumers.Values.Where(c => c.DataAsset.Id == dataAssetData.AssetId);
            var tasks = consumers.Select(c => TrySendDataAssetDataAsync(dataAssetData, c, string.Empty));
            await Task.WhenAll(tasks);
        }

        private async Task TrySendDataAssetDataAsync(DataAssetData dataAssetData, Consumer consumer, string client,
            bool consumeUnit = true)
        {
            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes())
            {
                ProviderSession? session = node.GetSession(consumer.DepositId);
                if (session is null)
                {
                    continue;
                }

                if (consumer.DataAsset.QueryType == QueryType.Stream)
                {
                    SessionClient? sessionClient = session.GetClient(client);
                    if (sessionClient is null || !sessionClient.StreamEnabled)
                    {
                        continue;
                    }
                }

                if (session.DataAvailability != DataAvailability.Available)
                {
                    continue;
                }

                await SendDataAssetDataAsync(dataAssetData, consumer, session, client, node.Peer, consumeUnit);
            }
        }

        private async Task SendDataAssetDataAsync(DataAssetData dataAssetData, Consumer consumer,
            ProviderSession session, string client, INdmProviderPeer ndmPeer, bool consumeUnit = true)
        {
            string? data = dataAssetData.Data;
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            if (session.DataAvailability != DataAvailability.Available)
            {
                return;
            }

            if (!consumer.HasAvailableUnits)
            {
                return;
            }

            if (!_depositManager.TryIncreaseSentUnits(consumer.DepositId))
            {
                return;
            }

            if (!_depositManager.HasAvailableUnits(consumer.DepositId))
            {
                consumer.SetUnavailableUnits();
                await _consumerRepository.UpdateAsync(consumer);
                return;
            }

            await SendDataAsync(consumer, session, ndmPeer, client, data, consumeUnit);
            DataAvailability dataAvailability = GetDataAvailability(consumer);
            if (dataAvailability == DataAvailability.Available)
            {
                return;
            }

            consumer.SetUnavailableUnits();
            await _consumerRepository.UpdateAsync(consumer);
            session.SetDataAvailability(dataAvailability);
            await _sessionRepository.UpdateAsync(session);
            ndmPeer.SendDataAvailability(session.DepositId, dataAvailability);
        }

        private async Task SendDataAsync(Consumer consumer, ProviderSession session, INdmProviderPeer ndmPeer,
            string client, string data, bool consumeUnit = true)
        {
            Keccak depositId = consumer.DepositId;
            switch (consumer.DataAsset.UnitType)
            {
                case DataAssetUnitType.Time:
                    uint now = (uint) _timestamper.UnixTime.Seconds;
                    uint currentlyConsumedUnits = now - consumer.VerificationTimestamp;
                    uint currentlyUnpaidUnits = currentlyConsumedUnits > session.PaidUnits
                        ? currentlyConsumedUnits - session.PaidUnits
                        : 0;
                    session.SetConsumedUnits((uint) (now - session.StartTimestamp));
                    session.SetUnpaidUnits(currentlyUnpaidUnits);
                    break;
                case DataAssetUnitType.Unit:
                    if (!consumeUnit)
                    {
                        break;
                    }

                    session.IncrementConsumedUnits();
                    session.IncrementUnpaidUnits();
                    break;
            }

            ndmPeer.SendDataAssetData(depositId, client, data, session.ConsumedUnits);
            await _sessionRepository.UpdateAsync(session);
            await _depositManager.HandleConsumedUnitAsync(depositId);
            if (_logger.IsTrace) _logger.Trace($"Sent data asset: '{consumer.DataAsset.Id}' data for deposit: '{depositId}', requested units: {consumer.DataRequest.Units}, consumed: {session.ConsumedUnits}, unpaid: {session.UnpaidUnits}, paid: {session.PaidUnits}.");
        }

        private DataAvailability GetDataAvailability(Consumer consumer)
        {
            DataAsset dataAsset = consumer.DataAsset;
            Keccak depositId = consumer.DepositId;
            uint consumedUnits = _depositManager.GetConsumedUnits(depositId);

            return _dataAvailabilityValidator.GetAvailability(dataAsset.UnitType, consumer.DataRequest.Units,
                consumedUnits, consumer.VerificationTimestamp, _timestamper.UnixTime.Seconds);
        }

        public async Task<Keccak?> SendEarlyRefundTicketAsync(Keccak depositId,
            RefundReason? reason = RefundReason.DataDiscontinued)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_providerAddress}' is locked, can't send an early refund ticket.");

                return null;
            }

            Consumer? consumer = await _consumerRepository.GetAsync(depositId);
            if (consumer is null)
            {
                if (_logger.IsInfo) _logger.Info($"Consumer has not been found for deposit: '{depositId}'.");

                return null;
            }

            IEnumerable<ConsumerNode> consumerNodes = _sessionManager.GetConsumerNodes();

            if(consumerNodes.Count() == 0)
            {
                if(_logger.IsInfo) _logger.Info($"No consumer nodes has been found.");

                return null;
            } 

            foreach (ConsumerNode node in consumerNodes)
            {
                ProviderSession? session = node.GetSession(depositId);
                if (session is null)
                {
                    continue;
                }

                SendEarlyRefundTicket(depositId, reason ?? RefundReason.DataDiscontinued, node.Peer);
            }

            return depositId;
        }

        public async Task<bool> ChangeDataAssetStateAsync(Keccak assetId, DataAssetState state)
        {
            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(assetId);
            if (dataAsset is null)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' was not found.");

                return false;
            }

            if (dataAsset.State == state)
            {
                return false;
            }

            if (dataAsset.State == DataAssetState.Archived)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' was already archived.");

                return false;
            }

            if (state == DataAssetState.Archived)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' cannot be archived (it has to be removed).");

                return false;
            }

            if (dataAsset.State == DataAssetState.Closed)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' was already closed.");

                return false;
            }

            dataAsset.SetState(state);
            await _dataAssetRepository.UpdateAsync(dataAsset);

            if (_dataAssets.TryGetValue(assetId, out DataAsset? inMemoryDataAsset))
            {
                inMemoryDataAsset.SetState(state);
            }

            var nodes = _sessionManager.GetConsumerNodes();
            switch (state)
            {
                case DataAssetState.Closed:
                    await SendEarlyRefundTicketsAsync();
                    break;
                case DataAssetState.Published:
                    SendDataAsset();
                    break;
            }

            SendStateChanged();

            async Task SendEarlyRefundTicketsAsync()
            {
                var consumers = await _consumerRepository.BrowseAsync(new GetConsumers
                {
                    AssetId = dataAsset.Id,
                    Results = int.MaxValue
                });
                foreach (Consumer consumer in consumers.Items)
                {
                    foreach (ConsumerNode node in nodes)
                    {
                        SendEarlyRefundTicket(consumer.DepositId, RefundReason.DataAssetStateChanged, node.Peer);
                    }
                }
            }

            void SendStateChanged()
            {
                foreach (ConsumerNode node in nodes)
                {
                    node.Peer.SendDataAssetStateChanged(dataAsset.Id, state);
                }
            }

            void SendDataAsset()
            {
                foreach (ConsumerNode node in nodes)
                {
                    node.Peer.SendDataAsset(dataAsset);
                }
            }

            return true;
        }

        public async Task<bool> ChangeDataAssetPluginAsync(Keccak assetId, string? plugin)
        {
            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(assetId);
            if (dataAsset is null)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' was not found.");
                return false;
            }

            if (dataAsset.State == DataAssetState.Published)
            {
                if (_logger.IsInfo) _logger.Info($"Data asset: '{assetId}' is published and plugin cannot be changed.");
                return false;
            }

            string? pluginName = plugin?.ToLowerInvariant();
            if (dataAsset.Plugin == pluginName)
            {
                return false;
            }

            string? previousPlugin = dataAsset.Plugin;
            dataAsset.SetPlugin(plugin);
            await _dataAssetRepository.UpdateAsync(dataAsset);
            if (_dataAssets.TryGetValue(assetId, out DataAsset? inMemoryDataAsset))
            {
                inMemoryDataAsset.SetPlugin(plugin);
            }

            if (_logger.IsInfo) _logger.Info($"Changed the plugin for data asset: '{dataAsset.Id}' from: '{previousPlugin}' to: '{plugin}'");

            return true;
        }

        public Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetProviderDepositApprovals query)
            => _depositApprovalRepository.BrowseAsync(query);

        public async Task<Keccak?> RequestDepositApprovalAsync(Keccak assetId, Address consumer, string kyc)
        {
            DataAsset? dataAsset = await _dataAssetRepository.GetAsync(assetId);
            if (dataAsset is null)
            {
                throw new ArgumentException($"Data asset with id: '{assetId}' was not found.");
            }

            Keccak id = DepositApproval.CalculateId(assetId, consumer);
            DepositApproval? approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                approval = new DepositApproval(assetId, dataAsset.Name, kyc, consumer, _providerAddress,
                    _timestamper.UnixTime.Seconds, DepositApprovalState.Pending);
                await _depositApprovalRepository.AddAsync(approval);

                return id;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was already requested by consumer: '{consumer}', state: '{approval.State}'");
            if (approval.State == DepositApprovalState.Pending)
            {
                return id;
            }

            var nodes = _sessionManager.GetConsumerNodes().Where(n => n.Peer.ConsumerAddress == consumer);
            switch (approval.State)
            {
                case DepositApprovalState.Confirmed:
                    foreach (ConsumerNode node in nodes)
                    {
                        node.Peer.SendDepositApprovalConfirmed(assetId, consumer);
                    }

                    break;
                case DepositApprovalState.Rejected:
                    foreach (ConsumerNode node in nodes)
                    {
                        node.Peer.SendDepositApprovalRejected(assetId, consumer);
                    }

                    break;
            }

            return id;
        }

        public Task<Keccak?> ConfirmDepositApprovalAsync(Keccak assetId, Address consumer)
            => UpdateDepositApprovalAsync(assetId, consumer, DepositApprovalState.Confirmed);

        public Task<Keccak?> RejectDepositApprovalAsync(Keccak assetId, Address consumer)
            => UpdateDepositApprovalAsync(assetId, consumer, DepositApprovalState.Rejected);

        private async Task<Keccak?> UpdateDepositApprovalAsync(Keccak assetId, Address consumer,
            DepositApprovalState state)
        {
            Keccak id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
            DepositApproval? approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}' was not found for consumer: '{consumer}'.");

                return null;
            }

            var nodes = _sessionManager.GetConsumerNodes().Where(n => n.Peer.ConsumerAddress == consumer);
            switch (state)
            {
                case DepositApprovalState.Confirmed:
                    approval.Confirm();
                    await _depositApprovalRepository.UpdateAsync(approval);
                    if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was confirmed.");
                    foreach (ConsumerNode node in nodes)
                    {
                        node.Peer.SendDepositApprovalConfirmed(assetId, consumer);
                    }

                    break;
                case DepositApprovalState.Rejected:
                    approval.Reject();
                    await _depositApprovalRepository.UpdateAsync(approval);
                    if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was rejected.");
                    foreach (ConsumerNode node in nodes)
                    {
                        node.Peer.SendDepositApprovalRejected(assetId, consumer);
                    }

                    break;
            }

            return assetId;
        }

        public async Task SendDepositApprovalsAsync(INdmProviderPeer peer, Keccak? dataAssetId = null,
            bool onlyPending = false)
        {
            var depositApprovals = await _depositApprovalRepository.BrowseAsync(new GetProviderDepositApprovals
            {
                Consumer = peer.ConsumerAddress,
                DataAssetId = dataAssetId,
                OnlyPending = onlyPending,
                Results = int.MaxValue
            });
            peer.SendDepositApprovals(depositApprovals.Items);
        }

        public Task InitPluginAsync(INdmPlugin plugin)
        {
            if (plugin.Name == null)
            {
                if(_logger.IsError) _logger.Error("Cannot initialize plugin that is missing name.");

                return Task.FromException(new ArgumentException($"Plugin is missing a name"));
            }
            
            plugin.InitAsync(_logManager);
            bool isAdded = _plugins.TryAdd(plugin.Name.ToLowerInvariant(), plugin);

            if(!isAdded)
            {
                if(_logger.IsError) _logger.Error($"Plugin {plugin.Name} was not added to the provider's plugins pool.");

                return Task.FromException(new InvalidOperationException($"Plugin {plugin.Name} was not added to the providers's plugins pool"));
            }

            if(_logger.IsInfo) _logger.Info($"{plugin.Name.ToLowerInvariant()} was added to the provider's plugins pool");
            ShowPluginsPool();

            return Task.CompletedTask;
        }

        public string[] GetPlugins() => _plugins.Keys.OrderBy(p => p).ToArray();

        private void SendEarlyRefundTicket(Keccak depositId, RefundReason reason, INdmProviderPeer peer)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_providerAddress}' is locked, can't send an early refund ticket.");

                return;
            }

            uint claimableAfter = _refundPolicy.GetClaimableAfterUnits(depositId);
            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _earlyRefundTicketAbiSig,
                depositId.Bytes, claimableAfter);
            Signature signature = _wallet.Sign(Keccak.Compute(abiHash), _providerAddress);
            peer.SendEarlyRefundTicket(new EarlyRefundTicket(depositId, claimableAfter, signature), reason);
            if (_logger.IsInfo) _logger.Info($"Sent early refund ticket for deposit: '{depositId}' to consumer: '{peer.NodeId}', reason: '{reason}'.");
        }

        private async Task<uint> VerifyDepositAsync(Keccak depositId, Address address)
        {
            if (_skipDepositVerification)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit verification disabled [id: '{depositId}', address: '{address}'].");
                return (uint) _timestamper.UnixTime.Seconds;
            }

            if (_logger.IsInfo) _logger.Info($"Verifying a deposit with id: '{depositId}' for an address: '{address}', required confirmations: {_requiredBlockConfirmations}.");
            uint verificationTimestamp = 0u;
            int confirmations = 0;
            Block? block = await _blockchainBridge.GetLatestBlockAsync();
            do
            {
                if (block == null)
                {
                    break;
                }

                (bool blockExists, uint timestamp) = await ConfirmDepositAsync(block!, depositId, address);
                if (!blockExists)
                {
                    if (_logger.IsWarn) _logger.Warn($"Verifying a deposit with id: '{depositId}' has failed - block was not found.");
                    return 0;
                }

                if (timestamp == 0)
                {
                    if (_logger.IsWarn) _logger.Warn($"Verifying a deposit with id: '{depositId}' has failed - verification timestamp is 0.");
                    return 0;
                }

                verificationTimestamp = timestamp;
                confirmations++;
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{depositId}' for an address: '{address}', has {confirmations}/{_requiredBlockConfirmations} confirmations.");
                if (confirmations == _requiredBlockConfirmations)
                {
                    break;
                }

                if (block.Timestamp < verificationTimestamp)
                {
                    break;
                }

                block = await _blockchainBridge.FindBlockAsync(block.ParentHash);
            } while (confirmations < _requiredBlockConfirmations);

            return verificationTimestamp;
        }

        private async Task<(bool blockExists, uint confirmationTimestamp)> ConfirmDepositAsync(Block block, Keccak depositId, Address address)
        {
            if (block is null)
            {
                if (_logger.IsWarn) _logger.Warn("Block was not found.");
                return (false, 0);
            }

            uint confirmationTimestamp = await _depositService.VerifyDepositAsync(address, depositId,
                block.Header.Number);
            if (confirmationTimestamp > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' has been confirmed in block number: {block.Number}, hash: '{block.Hash}'.");
                return (true, confirmationTimestamp);
            }

            if (_logger.IsError) _logger.Error($"Deposit: '{depositId}' has been rejected in block number: {block.Number}, hash: '{block.Hash}'.");
            return (true, 0);
        }

        private Keccak GetDepositId(DataRequest dataRequest)
        {
            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _depositAbiSig,
                dataRequest.DataAssetId.Bytes, dataRequest.Units, dataRequest.Value,
                dataRequest.ExpiryTime, dataRequest.Pepper, dataRequest.Provider, dataRequest.Consumer);

            return Keccak.Compute(abiHash);
        }

        private void SendInvalidData(Keccak depositId, InvalidDataReason reason)
        {
            if (depositId is null)
            {
                return;
            }

            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes(depositId))
            {
                node.Peer.SendInvalidData(depositId, reason);
            }
        }

        private void ShowPluginsPool()
        {
            if(!_logger.IsInfo)
            {
                return;
            }

            _logger.Info("Provider's data assets plugins: ");
            foreach(var plugin in _plugins)
            {
               _logger.Info(plugin.Key); 
            }
        }
    }
}