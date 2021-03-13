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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Shared.Services.Models;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class ConsumerServicesBackgroundProcessor : IConsumerServicesBackgroundProcessor, IDisposable
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly bool _useDepositTimer;
        private readonly IEthJsonRpcClientProxy? _ethJsonRpcClientProxy;
        private readonly IAccountService _accountService;
        private readonly IRefundClaimant _refundClaimant;
        private readonly IDepositConfirmationService _depositConfirmationService;
        private readonly IGasPriceService _gasPriceService;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;
        private readonly IPriceService _priceService;

        private Timer? _depositTimer;
        private Timer? _priceTimer;
        private uint _depositTimerPeriod;
        private uint _priceTimerPeriod;
        private long _currentBlockTimestamp;
        private long _currentBlockNumber;
        private string[] _currencies = {"USDT_DAI", "USDT_ETH"};

        public ConsumerServicesBackgroundProcessor(
            IAccountService accountService,
            IRefundClaimant refundClaimant,
            IDepositConfirmationService depositConfirmationService,
            IGasPriceService gasPriceService,
            IBlockProcessor blockProcessor,
            IDepositDetailsRepository depositRepository,
            IConsumerNotifier consumerNotifier,
            ILogManager logManager,
            IPriceService priceService,
            bool useDepositTimer = false,
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy = null,
            uint depositTimer = 10000, uint priceTimer = 10000)
        {
            _accountService = accountService;
            _refundClaimant = refundClaimant;
            _depositConfirmationService = depositConfirmationService;
            _gasPriceService = gasPriceService;
            _blockProcessor = blockProcessor;
            _consumerNotifier = consumerNotifier;
            _priceService = priceService;
            _depositRepository = depositRepository;
            _useDepositTimer = useDepositTimer;
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _depositTimerPeriod = depositTimer;
            _priceTimerPeriod = priceTimer;
            _logger = logManager.GetClassLogger();
            _gasPriceService.UpdateGasPriceAsync();
            _priceService.UpdateAsync(_currencies);
        }

        public void Init()
        {
            if (_useDepositTimer)
            {
                if (_depositTimer == null)
                {
                    if (_ethJsonRpcClientProxy == null)
                    {
                        if (_logger.IsError) _logger.Error("Cannot find any configured ETH proxy to run deposit timer.");
                        return;
                    }

                    _depositTimer = new Timer(_depositTimerPeriod);
                    _depositTimer.Elapsed += DepositTimerOnElapsed;
                    _depositTimer.Start();
                }

                if (_logger.IsInfo) _logger.Info("Initialized NDM consumer services background processor.");
            }
            else
            {
                _blockProcessor.BlockProcessed += OnBlockProcessed;
            }

            _priceTimer = new Timer(_priceTimerPeriod);
            _priceTimer.Elapsed += PriceTimerOnElapsed;
            _priceTimer.Start();
        }

        private void DepositTimerOnElapsed(object sender, ElapsedEventArgs e)
            => _ethJsonRpcClientProxy?.eth_getBlockByNumber(BlockParameterModel.Latest)
                .ContinueWith(async t =>
                {
                    if (t.IsFaulted && _logger.IsError)
                    {
                        _logger.Error("Fetching the latest block via proxy has failed.", t.Exception);
                        return;
                    }

                    BlockModel<Keccak>? block = t.Result?.IsValid == true ? t.Result.Result : null;
                    if (block is null)
                    {
                        _logger.Error("Latest block fetched via proxy is null.", t.Exception);
                        return;
                    }

                    if (_currentBlockNumber == block.Number)
                    {
                        return;
                    }

                    await ProcessBlockAsync((long) block.Number, (long) block.Timestamp);
                });

        private async void PriceTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            await _priceService.UpdateAsync(_currencies);
            foreach (var currency in _currencies)
            {
                var priceInfo = _priceService.Get(currency);
                if (priceInfo is null)
                {
                    continue;
                }

                await _consumerNotifier.SendUsdPriceAsync(currency, priceInfo.UsdPrice, priceInfo.UpdatedAt);
            }

            await _gasPriceService.UpdateGasPriceAsync();

            if (_gasPriceService.Types != null)
            {
                await _consumerNotifier.SendGasPriceAsync(_gasPriceService.Types);
            }
        }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
            => ProcessBlockAsync(e.Block.Number, (long) e.Block.Timestamp).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Processing the block {e.Block.Number} has failed.", t.Exception);
                }
            });

        private async Task ProcessBlockAsync(long number, long timestamp)
        {
            Interlocked.Exchange(ref _currentBlockNumber, number);
            Interlocked.Exchange(ref _currentBlockTimestamp, timestamp);
            await _consumerNotifier.SendBlockProcessedAsync(number);
            PagedResult<DepositDetails> depositsToConfirm = await _depositRepository.BrowseAsync(new GetDeposits
            {
                OnlyUnconfirmed = true,
                OnlyNotRejected = true,
                Results = int.MaxValue
            });

            await TryConfirmDepositsAsync(depositsToConfirm.Items);

            PagedResult<DepositDetails> depositsToRefund = await _depositRepository.BrowseAsync(new GetDeposits
            {
                EligibleToRefund = true,
                CurrentBlockTimestamp = _currentBlockTimestamp,
                Results = int.MaxValue
            });

            await TryClaimRefundsAsync(depositsToRefund.Items);
        }

        private async Task TryConfirmDepositsAsync(IReadOnlyList<DepositDetails> deposits)
        {
            if (!deposits.Any())
            {
                if (_logger.IsInfo) _logger.Info("No deposits to be verified have been found.");
                return;
            }

            foreach (DepositDetails deposit in deposits)
            {
                await _depositConfirmationService.TryConfirmAsync(deposit);
            }
        }

        private async Task TryClaimRefundsAsync(IReadOnlyList<DepositDetails> deposits)
        {
            if (!deposits.Any())
            {
                if (_logger.IsInfo) _logger.Info("No claimable refunds have been found.");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Found {deposits.Count} claimable refunds.");

            foreach (DepositDetails deposit in deposits)
            {
                Address refundTo = _accountService.GetAddress();
                RefundClaimStatus earlyRefundClaimStatus = await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
                if (earlyRefundClaimStatus.IsConfirmed)
                {
                    await _consumerNotifier.SendClaimedEarlyRefundAsync(deposit.Id, deposit.DataAsset.Name,
                        earlyRefundClaimStatus.TransactionHash!);
                }

                RefundClaimStatus refundClaimStatus = await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
                if (refundClaimStatus.IsConfirmed)
                {
                    await _consumerNotifier.SendClaimedRefundAsync(deposit.Id, deposit.DataAsset.Name,
                        refundClaimStatus.TransactionHash!);
                }
            }
        }

        public void Dispose()
        {
            _depositTimer?.Dispose();
        }
    }
}
