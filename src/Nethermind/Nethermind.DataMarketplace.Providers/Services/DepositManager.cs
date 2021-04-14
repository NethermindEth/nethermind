using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class DepositManager : IDepositManager
    {
        private const int LastClaimRetries = 3;
        private const int GasLimit = 70000;

        private readonly ConcurrentDictionary<Keccak, IDepositNodesHandler> _depositNodesHandlers =
            new ConcurrentDictionary<Keccak, IDepositNodesHandler>();

        private readonly IDepositNodesHandlerFactory _depositNodesHandlerFactory;
        private readonly ISessionManager _sessionManager;
        private readonly IReceiptsPolicies _receiptsPolicies;
        private readonly IWallet _wallet;
        private Address _providerAddress;
        private readonly IReceiptProcessor _receiptProcessor;
        private readonly IPaymentClaimProcessor _paymentClaimProcessor;
        private readonly IConsumerRepository _consumerRepository;
        private readonly IPaymentClaimRepository _paymentClaimRepository;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IProviderSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;
        private readonly IGasPriceService _gasPriceService;
        private readonly ILogger _logger;
        private bool _accountLocked;

        public DepositManager(IDepositNodesHandlerFactory depositNodesHandlerFactory, ISessionManager sessionManager,
            IReceiptsPolicies receiptsPolicies, IWallet wallet, Address providerAddress,
            IReceiptProcessor receiptProcessor, IPaymentClaimProcessor paymentClaimProcessor,
            IConsumerRepository consumerRepository, IPaymentClaimRepository paymentClaimRepository,
            IReceiptRepository receiptRepository, IProviderSessionRepository sessionRepository,
            ITimestamper timestamper, IGasPriceService gasPriceService, ILogManager logManager)
        {
            _depositNodesHandlerFactory = depositNodesHandlerFactory;
            _sessionManager = sessionManager;
            _receiptsPolicies = receiptsPolicies;
            _wallet = wallet;
            _providerAddress = providerAddress;
            _receiptProcessor = receiptProcessor;
            _paymentClaimProcessor = paymentClaimProcessor;
            _consumerRepository = consumerRepository;
            _paymentClaimRepository = paymentClaimRepository;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
            _gasPriceService = gasPriceService;
            _logger = logManager.GetClassLogger();
            _wallet.AccountLocked += OnAccountLocked;
            _wallet.AccountUnlocked += OnAccountUnlocked;
            _accountLocked = !_wallet.IsUnlocked(_providerAddress);
        }

        private void OnAccountUnlocked(object? sender, AccountUnlockedEventArgs e)
        {
            if (e.Address != _providerAddress)
            {
                return;
            }

            _accountLocked = false;
        }

        private void OnAccountLocked(object? sender, AccountLockedEventArgs e)
        {
            if (e.Address != _providerAddress)
            {
                return;
            }
            
            _accountLocked = true;
        }

        public async Task<IDepositNodesHandler> InitAsync(Keccak depositId, uint unpaidSessionUnits = 0)
        {
            if (_logger.IsInfo) _logger.Info($"Initializing deposit: '{depositId}'.");

            Consumer? consumer = await _consumerRepository.GetAsync(depositId);
            if (consumer is null)
            {
                if (_logger.IsInfo) _logger.Info($"Consumer has not been found for deposit: '{depositId}'.");

                return null;
            }

            if (_depositNodesHandlers.TryGetValue(depositId, out IDepositNodesHandler? deposit))
            {
                deposit.AddUnpaidUnits(unpaidSessionUnits);
                deposit.AddUnmergedUnits(unpaidSessionUnits);
                deposit.AddUnclaimedUnits(unpaidSessionUnits);

                if (_logger.IsInfo) _logger.Info($"Updated deposit: '{depositId}'.");

                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' has {deposit.UnpaidUnits} unpaid units, unmerged: {deposit.UnmergedUnits}, unclaimed: {deposit.UnclaimedUnits}, grace: {deposit.GraceUnits}.");

                return deposit;
            }
            else
            {
                uint purchasedUnits = consumer.DataRequest.Units;
                UInt256 unitPrice = consumer.DataAsset.UnitPrice;
                var sessions = await _sessionRepository.BrowseAsync(new GetProviderSessions
                {
                    DepositId = depositId,
                    Results = int.MaxValue
                });
                var depositReceipts = await _receiptRepository.BrowseAsync(depositId);
                
                var receipts = depositReceipts.OrderBy(r => r.Timestamp)
                    .ThenBy(r => r.Request.UnitsRange.To)
                    .ThenByDescending(r => r.Request.UnitsRange.From)
                    .ToArray();

                uint consumedUnits = (uint) sessions.Items.Sum(s => s.ConsumedUnits);
                uint graceUnits = (uint) sessions.Items.Sum(s => s.GraceUnits);
                uint unpaidUnits = (uint) (sessions.Items.Sum(s => s.UnpaidUnits) -
                                           sessions.Items.Sum(s => s.SettledUnits));

                ulong latestMergedReceiptTimestamp = receipts.LastOrDefault(r => r.IsMerged)?.Timestamp ?? 0;

                uint unmergedUnits = (uint) receipts.Where(r => !r.IsClaimed && !r.IsMerged &&
                                                                r.Timestamp >= latestMergedReceiptTimestamp)
                    .Sum(r => r.Request.UnitsRange.To - r.Request.UnitsRange.From + 1);

                ulong latestClaimedReceiptTimestamp = receipts.LastOrDefault(r => r.IsClaimed)?.Timestamp ?? 0;

                uint unclaimedUnits = (uint) receipts.Where(r => !r.IsClaimed && !r.IsMerged &&
                                                                 r.Timestamp >= latestClaimedReceiptTimestamp)
                    .Sum(r => r.Request.UnitsRange.To - r.Request.UnitsRange.From + 1);

                var latestReceipts = receipts.Where(r => r.Timestamp >= latestClaimedReceiptTimestamp);
                uint latestReceiptRequestNumber = receipts.Any() ? receipts.Max(r => r.Number) : 0;
                var paymentClaimsResult = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
                {
                    DepositId = depositId,
                    Page = 1,
                    Results = int.MaxValue
                });

                PaymentClaim latestPaymentClaim = paymentClaimsResult.Items.OrderBy(c => c.Timestamp)
                    .ThenBy(c => c.UnitsRange.To).LastOrDefault();

                deposit = _depositNodesHandlerFactory.CreateInMemory(depositId, consumer.DataRequest.Consumer,
                    consumer.DataAsset.UnitType, consumer.VerificationTimestamp, purchasedUnits, unitPrice,
                    consumedUnits, unpaidUnits, unmergedUnits, unclaimedUnits, graceUnits,
                    consumer.DataRequest.ExpiryTime, latestPaymentClaim, latestReceipts, latestReceiptRequestNumber);
                _depositNodesHandlers.TryAdd(depositId, deposit);
                if (_logger.IsInfo) _logger.Info($"Initialized deposit: '{depositId}'.");

                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' has {deposit.UnpaidUnits} unpaid units, unmerged: {deposit.UnmergedUnits}, unclaimed: {deposit.UnclaimedUnits}, grace: {deposit.GraceUnits}.");

                return deposit;
            }
        }

        public async Task HandleConsumedUnitAsync(Keccak depositId)
        {            
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit == null)
            {
                throw new InvalidDataException($"Cannot resolve deposit for deposit ID {depositId}");
            }
            
            switch (deposit.UnitType)
            {
                case DataAssetUnitType.Time:
                    uint consumedUnits = (uint) (_timestamper.UnixTime.Seconds - deposit.VerificationTimestamp);
                    uint paidUnits = deposit.ConsumedUnits - deposit.UnpaidUnits;
                    uint claimedUnits = deposit.ConsumedUnits - deposit.UnclaimedUnits;
                    uint mergedUnits = deposit.ConsumedUnits - deposit.UnmergedUnits;
                    uint unpaidUnits = consumedUnits > paidUnits
                        ? consumedUnits - paidUnits
                        : 0;
                    uint unclaimedUnits = consumedUnits > claimedUnits
                        ? consumedUnits - claimedUnits
                        : 0;
                    uint unmergedUnits = consumedUnits > mergedUnits
                        ? consumedUnits - mergedUnits
                        : 0;
                    deposit.SetConsumedUnits(consumedUnits);
                    deposit.SetUnpaidUnits(unpaidUnits);
                    deposit.SetUnclaimedUnits(unclaimedUnits);
                    deposit.SetUnmergedUnits(unmergedUnits);
                    break;
                case DataAssetUnitType.Unit:
                    deposit.IncrementConsumedUnits();
                    deposit.IncrementUnpaidUnits();
                    deposit.IncrementUnmergedUnits();
                    deposit.IncrementUnclaimedUnits();
                    break;
            }
            if (_logger.IsTrace) _logger.Trace($"Units consumed: {deposit.ConsumedUnits}, unpaid: {deposit.UnpaidUnits}, unmerged: {deposit.UnmergedUnits}, unclaimed: {deposit.UnclaimedUnits}.");
            
            if (!deposit.TryHandle())
            {
                if (_logger.IsTrace) _logger.Trace($"Already handling consumed unit for deposit: '{deposit.DepositId}'.");

                return;
            }
            
            try
            {
                await TryHandleDepositAsync(deposit);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"There was an error when trying to handle deposit: '{deposit.DepositId}'. {ex}", ex);
            }
            finally
            {
                deposit.FinishHandling();
            }
        }

        private async Task TryHandleDepositAsync(IDepositNodesHandler deposit, bool isRetry = false)
        {
            if (_logger.IsTrace) _logger.Trace($"Started handling consumed units for deposit: '{deposit.DepositId}'.");
            if (deposit.ConsumedAll || await _receiptsPolicies.CanRequestReceipts(deposit.UnpaidUnits, deposit.UnitPrice))
            {
                await RequestReceiptsAsync(deposit);
            }

            if (deposit.ConsumedAll || await _receiptsPolicies.CanMergeReceipts(deposit.UnmergedUnits, deposit.UnitPrice))
            {
                await TryMergeReceiptsAsync(deposit);
            }

            await TryClaimPaymentAsync(deposit);
            if (_logger.IsTrace) _logger.Trace($"Finished handling consumed units for deposit: '{deposit.DepositId}'.");
            if (isRetry)
            {
                return;
            }
            
            if (!deposit.ConsumedAll)
            {
                return;
            }

            if (deposit.HasClaimedAllUnits)
            {
                if (_logger.IsInfo) _logger.Info($"Last payment claim for deposit: '{deposit.DepositId}' was already processed.");

                return;
            }

            deposit.FinishHandling();
            while (deposit.CurrentLastClaimRetry < LastClaimRetries)
            {
                if (_logger.IsInfo) _logger.Info($"Missing payment claim for deposit: '{deposit.DepositId}', retry {deposit.CurrentLastClaimRetry + 1}/{LastClaimRetries}.");
                foreach (ConsumerNode node in _sessionManager.GetConsumerNodes(deposit.DepositId))
                {
                    ProviderSession? session = node.GetSession(deposit.DepositId);
                    if (session is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Session was not found for deposit: '{deposit.DepositId}', node: '{node.Peer.NodeId}'.");
                        continue;
                    }                    
                    
                    if (_logger.IsInfo) _logger.Info($"Consumer: '{node.Peer.NodeId}', session: '{session.Id}' has {session.UnpaidUnits} unpaid units.");
                }
                await TryHandleDepositAsync(deposit, true);
                deposit.IncrementLastClaimRetries();
                if (deposit.HasClaimedAllUnits)
                {
                    return;
                }
            }
        }

        public async Task HandleUnpaidUnitsAsync(Keccak depositId, INdmProviderPeer peer)
        {
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit == null)
            {
                throw new InvalidDataException($"Cannot resolve deposit for deposit ID {depositId}");
            }
            
            ProviderSession? currentSession = _sessionManager.GetSession(depositId, peer);
            if (currentSession is null)
            {
                if(_logger.IsError) _logger.Error($"Cannot handle unpaid units due to missing session for deposit {depositId}");
                throw new InvalidDataException("Cannot handle unpaid units due to missing session for deposit {depositId}");
            }
            
            if (currentSession.UnpaidUnits > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Consumer: '{peer.NodeId}', deposit: '{depositId}' has {currentSession.UnpaidUnits} unpaid units from current session.");
                deposit.AddUnpaidUnits(currentSession.UnpaidUnits);
                await TryHandleReceiptAsync(currentSession, deposit.Consumer, peer);
            }

            ProviderSession? previousSession = await _sessionRepository.GetPreviousAsync(currentSession);
            if (previousSession?.UnpaidUnits > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Consumer: '{peer.ConsumerAddress}', deposit: '{depositId}' has {previousSession.UnpaidUnits} unpaid units from previous session: '{previousSession.Id}'.");
                deposit.AddUnpaidUnits(previousSession.UnpaidUnits);
                await TryHandleReceiptAsync(currentSession, deposit.Consumer, peer, previousSession);
            }
            
            if (_logger.IsTrace) _logger.Trace($"Deposit: '{depositId}' has {deposit.UnpaidUnits} unpaid units, unmerged: {deposit.UnmergedUnits}, unclaimed: {deposit.UnclaimedUnits}.");
            
            if (deposit.ConsumedAll || await _receiptsPolicies.CanMergeReceipts(deposit.UnmergedUnits, deposit.UnitPrice))
            {
                await TryMergeReceiptsAsync(deposit);
            }

            await TryClaimPaymentAsync(deposit);
        }

        public uint GetConsumedUnits(Keccak depositId)
        {
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit is null)
            {
                return 0;
            }

            switch (deposit.UnitType)
            {
                case DataAssetUnitType.Unit:
                {
                    return deposit.ConsumedUnits;
                }
                case DataAssetUnitType.Time:
                {
                    return (uint) _timestamper.UnixTime.Seconds - deposit.VerificationTimestamp;
                }
                default: return 0;
            }
        }

        public bool HasAvailableUnits(Keccak depositId)
        {
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit is null)
            {
                return false;
            }

            return !deposit.ConsumedAll && !deposit.IsExpired((uint) _timestamper.UnixTime.Seconds);
        }

        public bool TryIncreaseSentUnits(Keccak depositId) =>
            GetDepositNodesHandler(depositId)?.TryIncreaseSentUnits() ?? false;

        public void ChangeAddress(Address address)
        {
            _providerAddress = address;
            _accountLocked = !_wallet.IsUnlocked(_providerAddress);
        }

        public void ChangeColdWalletAddress(Address address) => _paymentClaimProcessor.ChangeColdWalletAddress(address);

        public bool IsExpired(Keccak depositId)
            => GetDepositNodesHandler(depositId)?.IsExpired((uint) _timestamper.UnixTime.Seconds) ?? false;

        private async Task TryClaimPaymentAsync(IDepositNodesHandler deposit)
        {
            if (_accountLocked)
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{_providerAddress}' is locked, can't claim a payment.");
                
                return;
            }
            
            if (!deposit.Receipts.Any())
            {
                return;
            }
            
            if (deposit.HasClaimedAllUnits)
            {
                if (_logger.IsInfo) _logger.Info($"Last payment was already claimed for deposit: '{deposit.DepositId}'.");

                return;
            }
            
            try
            {
                var unclaimedUnits = GetUnclaimedUnits(deposit);
                if (deposit.ConsumedAll || await _receiptsPolicies.CanClaimPayment(unclaimedUnits, deposit.UnitPrice))
                {
                    UInt256 gasPrice = await _gasPriceService.GetCurrentGasPriceAsync();
                    UInt256 fee = gasPrice * GasLimit;
                    UInt256 profit = unclaimedUnits * deposit.UnitPrice;
                    if (fee >= profit)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Claiming a payment would cause loss (fee: {fee} wei >= profit: {profit} wei).");
                        return;
                    }
                    
                    await ClaimPaymentAsync(deposit);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when claiming a payment for deposit: '{deposit.DepositId}'. {ex}");
            }
        }

        public uint GetUnclaimedUnits(Keccak depositId)
        {
            var deposit = GetDepositNodesHandler(depositId);
            return deposit is null ? 0 : GetUnclaimedUnits(deposit);
        }

        private uint GetUnclaimedUnits(IDepositNodesHandler deposit)
        {
            var latestReceiptUnitsRange = deposit.LatestReceipt?.IsClaimed == true 
                                            ? null
                                            : deposit.LatestReceipt?.Request.UnitsRange; 

            var unclaimedRange = deposit.LatestMergedReceipt?.IsClaimed == true
                ? latestReceiptUnitsRange
                : deposit.LatestMergedReceipt?.Request.UnitsRange;

            return unclaimedRange is null ? 0 : unclaimedRange.To - unclaimedRange.From + 1;
        }

        private async Task RequestReceiptsAsync(IDepositNodesHandler deposit)
        {
            Keccak depositId = deposit.DepositId;
            int nodesCount = _sessionManager.GetNodesCount(depositId);
            int nodesCounter = 0;
            foreach (ConsumerNode node in _sessionManager.GetConsumerNodes(depositId))
            {               
                nodesCounter++;
                if (deposit.HasSentAllReceipts)
                {
                    if (_logger.IsInfo) _logger.Info($"Last receipt request for deposit: '{depositId}' was already sent.");
                    return;
                }

                if (deposit.HasClaimedAllUnits)
                {
                    if (_logger.IsInfo) _logger.Info($"Last receipt request for deposit: '{depositId}' was already claimed.");
                    return;
                }

                ProviderSession? session = node.GetSession(depositId);
                if (session is null)
                {
                    if (_logger.IsInfo) _logger.Info($"Session was not found for deposit: '{depositId}', node: '{node.Peer.NodeId}'.");
                    continue;
                }
                
                if (session.UnpaidUnits == 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Session: '{session.Id}' has no unpaid units.");
                    continue;
                }
                
                if (_logger.IsTrace) _logger.Trace($"Session: '{session.Id}' has {session.UnpaidUnits} unpaid units.");
                if (!deposit.ConsumedAll && await _receiptsPolicies.CanRequestReceipts(session.UnpaidUnits * nodesCount, deposit.UnitPrice) == false)
                {
                    if (_logger.IsTrace) _logger.Trace($"Session: '{session.Id}' has too low unpaid units to be processed.");
                    continue;
                }
                              
                if (_logger.IsInfo) _logger.Info($"Requesting receipt for deposit: '{depositId}' from node: '{node.Peer.NodeId}' ({nodesCounter}/{nodesCount}).");
                DataDeliveryReceiptDetails? details = await TryHandleReceiptAsync(session, deposit.Consumer, node.Peer);
                if (details is null)
                {
                    if (_logger.IsInfo) _logger.Info($"Couldn't request receipt for deposit: '{depositId}' from node: '{node.Peer.NodeId}'.");
                    
                    continue;
                }
                
                if (_logger.IsInfo) _logger.Info($"Successfully requested receipt for deposit: '{depositId}' from node: '{node.Peer.NodeId}'.");
            }
        }

        private async Task<DataDeliveryReceiptDetails?> TryHandleReceiptAsync(ProviderSession session, Address consumer,
            INdmProviderPeer peer, ProviderSession? previousSession = null)
        {
            Keccak depositId = session.DepositId;
            try
            {
                bool isSettlement = !(previousSession is null);
                uint unpaidSessionUnits = previousSession?.UnpaidUnits ?? session.UnpaidUnits;
                DataDeliveryReceiptRequest request = CreateRequest(depositId, session.Id, unpaidSessionUnits, isSettlement);
                
                return await TryHandleReceiptAsync(session, consumer, peer, request);  
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"There was an error when handling the receipts for deposit: '{depositId}', node: '{peer.NodeId}'.{ex}", ex);
            }

            return null;
        }

        private DataDeliveryReceiptRequest CreateRequest(Keccak depositId, Keccak sessionId, uint unpaidSessionUnits,
            bool isSettlement = false)
        {
            if (_logger.IsInfo) _logger.Info($"Creating a receipt request for deposit: '{depositId}', session: '{sessionId}'.");
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit == null)
            {
                throw new InvalidDataException($"Cannot resolve deposit for deposit ID {depositId}");
            }
            
            uint number = deposit.GetNextReceiptRequestNumber();
            uint latestReceiptRangeTo = deposit.LatestReceipt?.Request.UnitsRange.To ?? 0;
            uint rangeFrom = deposit.LatestReceipt is null ? 0 : latestReceiptRangeTo + 1;
            uint rangeTo = rangeFrom + unpaidSessionUnits - 1;
            rangeTo = rangeTo >= deposit.PurchasedUnits ? (uint) deposit.PurchasedUnits - 1 : rangeTo;
            UnitsRange unitsRange = new UnitsRange(rangeFrom, rangeTo);
            if (_logger.IsInfo) _logger.Info($"Created a receipt request for deposit: '{depositId}', session: '{sessionId}', range: [{unitsRange.From}, {unitsRange.To}].");

            return new DataDeliveryReceiptRequest(number, depositId, unitsRange, isSettlement);
        }

        private async Task TryMergeReceiptsAsync(IDepositNodesHandler deposit)
        {
            Keccak depositId = deposit.DepositId;
            if (deposit.HasSentLastMergedReceipt)
            {
                if (_logger.IsInfo) _logger.Info($"Last receipt request for deposit: '{depositId}' was already sent.");
                return;
            }
            
            if (deposit.HasClaimedAllUnits)
            {
                if (_logger.IsInfo) _logger.Info($"Last receipt request for deposit: '{depositId}' was already claimed.");
                return;
            }
            
            DataDeliveryReceiptDetails? latestReceipt = deposit.LatestMergedReceipt;
            if (latestReceipt?.Request.UnitsRange.To == deposit.PurchasedUnits)
            {
                if (_logger.IsInfo)  _logger.Info($"Last receipt request for deposit: '{depositId}' was already merged.");
                return;
            }

            var consumerNodes = _sessionManager.GetConsumerNodes(depositId);
            foreach (ConsumerNode node in consumerNodes)
            {
                if (_logger.IsInfo) _logger.Info($"Trying to merge receipts for deposit: '{depositId}' with node: '{node.Peer.NodeId}'.");
                try
                {                    
                    ProviderSession? session = node.GetSession(depositId);
                    if (session is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Session was not found for deposit: '{depositId}', node: '{node.Peer.NodeId}'.");
                        continue;
                    }
                    
                    DataDeliveryReceiptRequest? request = CreateMergedRequest(deposit);
                    if (request is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Merged receipt for deposit: '{depositId}' couldn't be created.");
                        return;
                    }
                    
                    if (latestReceipt?.Request.UnitsRange.Equals(request.UnitsRange) == true)
                    {
                        if (_logger.IsInfo) _logger.Info($"Merged receipt request for deposit: '{depositId}' would be the same as a previous one (already sent).");
                        continue;
                    }
                
                    uint previouslyMergedUnits = latestReceipt?.Request.UnitsRange.To + 1 ?? 0;
                    DataDeliveryReceiptDetails? receiptDetails = await TryHandleReceiptAsync(session, deposit.Consumer, node.Peer, request);
                    if (receiptDetails is null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Couldn't merge receipts for deposit: '{depositId}' with node: '{node.Peer.NodeId}'.");
                        continue;
                    }
                
                    deposit.AddReceipt(receiptDetails);
                    UnitsRange range = receiptDetails.Request.UnitsRange;
                    uint mergedTo = range.To + 1;
                    if (mergedTo <= previouslyMergedUnits)
                    {
                        return;
                    }
                    
                    uint mergedUnits = mergedTo - previouslyMergedUnits;
                    mergedUnits = deposit.UnmergedUnits < mergedUnits ? deposit.UnmergedUnits : mergedUnits;
                    deposit.SubtractUnmergedUnits(mergedUnits);
                    if (_logger.IsInfo) _logger.Info($"Successfully merged receipts ({mergedUnits} units) for deposit: '{depositId}' with node: '{node.Peer.NodeId}'.");
                    break;
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"There was an error when merging receipt requests for deposit: '{deposit.DepositId}'. {ex}");
                }
            }
        }

        private async Task ClaimPaymentAsync(IDepositNodesHandler deposit)
        {
            if (deposit.LatestPaymentClaim?.UnitsRange.To == deposit.PurchasedUnits - 1)
            {
                if (_logger.IsInfo) _logger.Info($"Last receipt request for deposit: '{deposit.DepositId}' was already claimed.");
                return;
            }
            
            var claimableReceipts = deposit.Receipts
                .Where(r => !r.IsClaimed)
                .OrderBy(r => r.Timestamp)
                .ThenBy(r => r.Request.UnitsRange.To)
                .ToArray();

            DataDeliveryReceiptDetails latestClaimableReceipt = claimableReceipts.LastOrDefault(r => r.IsMerged) ??
                                                                claimableReceipts.FirstOrDefault(r =>
                                                                    r.Request.UnitsRange.To == deposit.PurchasedUnits - 1);

            if (latestClaimableReceipt is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot claim a payment for deposit: '{deposit.DepositId}' - claimable receipt was not found.");
                return;
            }
            
            if (latestClaimableReceipt.IsClaimed)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot claim a payment for deposit: '{deposit.DepositId}' - receipt was already claimed, timestamp: {latestClaimableReceipt.Timestamp}.");
                return;
            }

            DataDeliveryReceiptDetails? receipt = await _receiptRepository.GetAsync(latestClaimableReceipt.Id);
            if (receipt == null)
            {
                throw new InvalidDataException($"Unable to make a claim for the receipt with ID: {latestClaimableReceipt.Id} - receipt missing");
            }
            
            receipt.Claim();
            await _receiptRepository.UpdateAsync(receipt);
            PaymentClaim? paymentClaim = await _paymentClaimProcessor.ProcessAsync(latestClaimableReceipt.Request,
                latestClaimableReceipt.Receipt.Signature);
            if (paymentClaim is null)
            {
                throw new InvalidDataException($"Unable to make a claim for the receipt with ID: {latestClaimableReceipt.Id} - claim processing failure");
            }
            
            latestClaimableReceipt.Claim();
            deposit.AddReceipt(latestClaimableReceipt); // so the receipt become LatestReceipt 
            deposit.ClearReceipts();
            deposit.SetLatestPaymentClaim(paymentClaim);
            uint claimedUnits = deposit.UnclaimedUnits < paymentClaim.ClaimedUnits
                ? deposit.UnclaimedUnits
                : paymentClaim.ClaimedUnits;
            deposit.SubtractUnclaimedUnits(claimedUnits);
            if (_logger.IsInfo) _logger.Info($"Successfully claimed payment ({claimedUnits} units) for deposit: '{deposit.DepositId}'.");
        }

        private DataDeliveryReceiptRequest? CreateMergedRequest(IDepositNodesHandler deposit)
        {
            if (deposit.LatestReceipt == null)
            {
                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Creating merged receipt request for deposit: '{deposit.DepositId}'.");
            bool isLastReceipt = deposit.LatestReceipt.Request.UnitsRange.To == deposit.PurchasedUnits - 1;

            if (isLastReceipt && _logger.IsInfo) _logger.Info($"Merged receipt request for deposit: '{deposit.DepositId}' will be the last one.");

            uint latestPaymentClaimRangeTo = deposit.LatestPaymentClaim?.UnitsRange.To ?? 0;

            DataDeliveryReceiptDetails? latestMergedReceipt = deposit.Receipts.OrderBy(r => r.Timestamp)
                .ThenBy(r => r.Request.UnitsRange.To)
                .LastOrDefault(r => r.IsMerged);

            uint latestMergedReceiptRangeFrom = latestMergedReceipt?.Request.UnitsRange.From ?? 0;
            long latestMergedReceiptRangeTo = latestMergedReceipt?.Request.UnitsRange.To ?? 0;

            var mergeableReceipts = deposit.Receipts
                .Where(r => r.Timestamp >= (latestMergedReceipt?.Timestamp ?? 0) &&
                            r.Request.UnitsRange.To >
                            (latestMergedReceipt is null ? -1 : latestMergedReceiptRangeTo))
                .OrderBy(r => r.Timestamp)
                .ThenBy(r => r.Request.UnitsRange.To)
                .ToList();
            
            var paymentClaimExist = deposit.LatestPaymentClaim is { };
            uint rangeFrom;

            if (paymentClaimExist)
            {
                rangeFrom = latestPaymentClaimRangeTo + 1;
            }
            else
            {
                if (latestMergedReceiptRangeFrom > 0)
                {
                    rangeFrom = latestMergedReceiptRangeFrom + 1;
                }
                else
                {
                    rangeFrom = 0;
                }
            }

            uint rangeTo = deposit.LatestReceipt.Request.UnitsRange.To;
            if (rangeFrom > rangeTo)
            {
                return null;
            }
            
            if (!(latestMergedReceipt is null))
            {
                mergeableReceipts.Insert(0, latestMergedReceipt);
            }
            
            UnitsRange unitsRange = new UnitsRange(rangeFrom, rangeTo);
            var receiptsToMerge = mergeableReceipts.Select(r => new DataDeliveryReceiptToMerge(
                r.Request.UnitsRange, r.Receipt.Signature)).OrderBy(r => r.UnitsRange.To).ToList();
            uint number = deposit.GetNextReceiptRequestNumber();

            if (_logger.IsInfo) _logger.Info($"Created merged receipt request for deposit: '{deposit.DepositId}' [{rangeFrom}, {rangeTo}].");

            return new DataDeliveryReceiptRequest(number, deposit.DepositId, unitsRange,
                receiptsToMerge: receiptsToMerge);
        }

        private async Task<DataDeliveryReceiptDetails?> TryHandleReceiptAsync(ProviderSession session, Address consumer,
            INdmProviderPeer peer, DataDeliveryReceiptRequest request)
        {
            Keccak depositId = session.DepositId;
            IDepositNodesHandler? deposit = GetDepositNodesHandler(depositId);
            if (deposit == null)
            {
                throw new InvalidDataException($"Cannot resolve deposit handle for deposit ID: {depositId}");
            }
            
            try
            {
                UnitsRange unitsRange = request.UnitsRange;
                if (_logger.IsInfo) _logger.Info($"Sending data delivery receipt request ({request.Number}), deposit: '{depositId}', session: '{session.Id}', range: [{unitsRange.From}, {unitsRange.To}].");
                if (request.ReceiptsToMerge.Any())
                {
                    if (_logger.IsInfo) _logger.Info($"Receipts to merge for deposit: '{depositId}': {string.Join(", ", request.ReceiptsToMerge.Select(r => $"[{r.UnitsRange.From}, {r.UnitsRange.To}]"))}");
                }

                (DataDeliveryReceipt receipt, RequestReceiptStatus status) = await TryHandleDeliveryReceipt(deposit, consumer, session, request, peer);
                Keccak id = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(request.Number)).Bytes);
                DataDeliveryReceiptDetails receiptDetails = new DataDeliveryReceiptDetails(id, session.Id, session.DataAssetId, peer.NodeId,
                    request, receipt, _timestamper.UnixTime.Seconds, false);
                await _receiptRepository.AddAsync(receiptDetails);
                if (status != RequestReceiptStatus.Ok)
                {
                    return null;
                }

                UnitsRange range = request.UnitsRange;
                uint claimedUnits = range.To - range.From + 1;
                deposit.AddReceipt(receiptDetails);
                if (!request.ReceiptsToMerge.Any())
                {
                    deposit.SubtractUnpaidUnits(claimedUnits);
                }

                if (_logger.IsInfo) _logger.Info($"Successfully processed receipt ({claimedUnits} units) for deposit: '{depositId}' with node: '{peer.NodeId}'.");

                return receiptDetails;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"There was an error when processing a receipt for deposit: '{depositId}'. {ex}");
                session.SetDataAvailability(DataAvailability.DataDeliveryReceiptNotProvided);
                await _sessionRepository.UpdateAsync(session);
                peer.SendDataAvailability(depositId, DataAvailability.DataDeliveryReceiptInvalid);

                return null;
            }
        }

        private async Task<(DataDeliveryReceipt, RequestReceiptStatus)> TryHandleDeliveryReceipt(IDepositNodesHandler deposit,
            Address consumer, ProviderSession session, DataDeliveryReceiptRequest request, INdmProviderPeer peer,
            bool isRetry = false)
        {
            while (true)
            {
                Keccak depositId = session.DepositId;
                DataDeliveryReceipt receipt = await peer.SendRequestDataDeliveryReceiptAsync(request, CancellationToken.None);
                if (_logger.IsInfo) _logger.Info($"Received a delivery receipt for deposit: '{depositId}' from node: '{peer.NodeId}', status code: '{receipt.StatusCode}'.");
                if (isRetry && receipt.StatusCode != StatusCodes.Ok)
                {
                    if (_logger.IsWarn) _logger.Warn($"Request receipt retry failed for: '{depositId}' from node: '{peer.NodeId}', status code: '{receipt.StatusCode}'.");

                    return (receipt, RequestReceiptStatus.Invalid);
                }

                switch (receipt.StatusCode)
                {
                    case StatusCodes.Ok:
                        if (await _receiptProcessor.TryProcessAsync(session, consumer, peer, request, receipt))
                        {
                            return (receipt, RequestReceiptStatus.Ok);
                        }

                        return (receipt, RequestReceiptStatus.Invalid);
                    case StatusCodes.InvalidReceiptRequestRange:
                    {
                        if (_logger.IsInfo) _logger.Info($"Consumer for deposit: '{depositId}' from node: '{peer.NodeId}' claims {receipt.ConsumedUnits} consumed and {receipt.UnpaidUnits} unpaid units.");
                        UnitsRange range = request.UnitsRange;
                        uint requestedUnits = range.To - range.From + 1;
                        if (requestedUnits <= receipt.UnpaidUnits)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Consumer for deposit: '{depositId}' from node: '{peer.NodeId}' claimed an invalid range  (while it was actually valid).");

                            break;
                        }
                        
                        uint graceUnits = requestedUnits - receipt.UnpaidUnits;
                        uint totalGraceUnits = graceUnits + deposit.GraceUnits;
                        bool hasReachedThreshold = await _receiptsPolicies.CanClaimPayment(totalGraceUnits, deposit.UnitPrice);
                        if (hasReachedThreshold)
                        {
                            peer.SendGraceUnitsExceeded(depositId, deposit.ConsumedUnits, totalGraceUnits);
                            if (_logger.IsWarn) _logger.Warn($"Consumer for deposit: '{depositId}' from node: '{peer.NodeId}' claimed too many unpaid units, grace units exceeded ({totalGraceUnits}).");
                            
                            return (receipt, RequestReceiptStatus.GraceUnitsExceeded);
                        }

                        if (_logger.IsInfo) _logger.Info($"Unpaid units difference is: {graceUnits} ({requestedUnits} - {receipt.UnpaidUnits}). Lowering units amount for deposit: '{depositId}'.");
                        deposit.SubtractUnpaidUnits(graceUnits);
                        deposit.SubtractUnmergedUnits(graceUnits);
                        deposit.SubtractUnclaimedUnits(graceUnits);
                        deposit.AddGraceUnits(graceUnits);
                        session.SubtractUnpaidUnits(graceUnits);
                        session.AddGraceUnits(graceUnits);
                        if (range.To < graceUnits)
                        {
                            peer.SendGraceUnitsExceeded(depositId, deposit.ConsumedUnits, totalGraceUnits);
                            if (_logger.IsWarn) _logger.Warn($"Cannot request a receipt for deposit: '{depositId}'  - grace units amount is greater than the receipt range ({graceUnits} > {range.To}).");
                            
                            return (receipt, RequestReceiptStatus.GraceUnitsExceeded);
                        }
                        
                        uint updatedRangeTo = range.To - graceUnits;
                        if (range.From > updatedRangeTo)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Invalid updated range [{range.From}, {updatedRangeTo}] for: '{depositId}' - receipt request will not be send.");
                            
                            return (receipt, RequestReceiptStatus.Invalid);
                        }
                        
                        if (_logger.IsInfo) _logger.Info($"Grace units for deposit: '{depositId}' is: {deposit.GraceUnits} (added {graceUnits}).");
                        UnitsRange updatedRange = new UnitsRange(range.From, updatedRangeTo);
                        if (_logger.IsInfo) _logger.Info($"Updated range for deposit: '{depositId}' [{updatedRange.From}, {updatedRange.To}]. Requesting receipt once again.");
                        request = request.WithRange(updatedRange, deposit.GetNextReceiptRequestNumber());
                        isRetry = true;
                        continue;
                    }
                    case StatusCodes.InvalidReceiptAddress:
                    {
                        break;
                    }
                    case StatusCodes.Error:
                    {
                        break;
                    }
                }

                return (receipt, RequestReceiptStatus.Invalid);
            }
        }

        private IDepositNodesHandler? GetDepositNodesHandler(Keccak id)
            => _depositNodesHandlers.TryGetValue(id, out IDepositNodesHandler? deposit) ? deposit : null;

        private enum RequestReceiptStatus
        {
            Invalid,
            GraceUnitsExceeded,
            Ok
        }
    }
}