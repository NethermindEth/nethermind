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

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class ConsumerServicesBackgroundProcessor : IConsumerServicesBackgroundProcessor
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly bool _useDepositTimer;
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IAccountService _accountService;
        private readonly IRefundClaimant _refundClaimant;
        private readonly IDepositConfirmationService _depositConfirmationService;
        private readonly IBlockProcessor _blockProcessor;
        private readonly Timer _refundClaimTimer;
        private readonly Timer _depositTimer;
        private readonly ILogger _logger;
        private long _currentBlockTimestamp;
        private long _currentBlockNumber;

        public ConsumerServicesBackgroundProcessor(IAccountService accountService, IRefundClaimant refundClaimant,
            IDepositConfirmationService depositConfirmationService, IBlockProcessor blockProcessor,
            IDepositDetailsRepository depositRepository, IConsumerNotifier consumerNotifier, ILogManager logManager,
            uint tryClaimRefundsIntervalMilliseconds = 60000, bool useDepositTimer = false, 
            IEthJsonRpcClientProxy ethJsonRpcClientProxy = null, uint depositTimer = 10000)
        {
            _accountService = accountService;
            _refundClaimant = refundClaimant;
            _depositConfirmationService = depositConfirmationService;
            _blockProcessor = blockProcessor;
            _depositRepository = depositRepository;
            _consumerNotifier = consumerNotifier;
            _useDepositTimer = useDepositTimer;
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _logger = logManager.GetClassLogger();
            _refundClaimTimer = new Timer(tryClaimRefundsIntervalMilliseconds);
            if (_useDepositTimer)
            {
                _depositTimer = new Timer(depositTimer);
            }
        }

        public void Init()
        {
            if (_useDepositTimer)
            {
                _depositTimer.Elapsed += DepositTimerOnElapsed;
                _depositTimer.Start();
            }
            else
            {
                _blockProcessor.BlockProcessed += OnBlockProcessed;
            }

            _refundClaimTimer.Elapsed += RefundClaimTimerOnElapsed;
            _refundClaimTimer.Start();
            if (_logger.IsInfo) _logger.Info("Initialized NDM consumer services background processor.");
        }

        private void DepositTimerOnElapsed(object sender, ElapsedEventArgs e)
            => _ethJsonRpcClientProxy.eth_getBlockByNumber(BlockParameterModel.Latest)
                .ContinueWith(async t =>
                {
                    if (t.IsFaulted && _logger.IsError)
                    {
                        _logger.Error("Fetching the latest block via proxy has failed.", t.Exception);
                        return;
                    }

                    var block = t.Result.IsValid ? t.Result.Result : null;
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

        private void RefundClaimTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
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
                        _logger.Error("Fetching the deposits has failed.", t.Exception);
                        return;
                    }

                    if (t.Result.Items.Any())
                    {
                        if (_logger.IsInfo) _logger.Info("No claimable refunds have been found.");
                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Found {t.Result.Items.Count} claimable refunds.");
                    var refundTo = _accountService.GetAddress();
                    foreach (var deposit in t.Result.Items)
                    {
                        await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
                        await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
                    }
                });
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
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
            var deposits = await _depositRepository.BrowseAsync(new GetDeposits
            {
                OnlyUnconfirmed = true,
                OnlyNotRejected = true,
                Results = int.MaxValue
            });
            await TryConfirmDepositsAsync(deposits.Items);
        }

        private async Task TryConfirmDepositsAsync(IEnumerable<DepositDetails> deposits)
        {
            foreach (var deposit in deposits)
            {
                await _depositConfirmationService.TryConfirmAsync(deposit).ContinueWith(t =>
                {
                    if (t.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Confirming a deposit with id: '{deposit.Id}' has failed.",
                            t.Exception);
                    }
                });
            }
        }
    }
}