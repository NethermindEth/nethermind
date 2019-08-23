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
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class ConsumerServicesBackgroundProcessor : IConsumerServicesBackgroundProcessor
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly IAccountService _accountService;
        private readonly IRefundClaimant _refundClaimant;
        private readonly IDepositConfirmationService _depositConfirmationService;
        private readonly IBlockProcessor _blockProcessor;
        private readonly Timer _timer;
        private readonly ILogger _logger;
        private long _currentBlockTimestamp;

        public ConsumerServicesBackgroundProcessor(IAccountService accountService, IRefundClaimant refundClaimant,
            IDepositConfirmationService depositConfirmationService, IBlockProcessor blockProcessor,
            IDepositDetailsRepository depositRepository, IConsumerNotifier consumerNotifier, ILogManager logManager,
            uint tryClaimRefundsIntervalMilliseconds = 60000)
        {
            _accountService = accountService;
            _refundClaimant = refundClaimant;
            _depositConfirmationService = depositConfirmationService;
            _blockProcessor = blockProcessor;
            _depositRepository = depositRepository;
            _consumerNotifier = consumerNotifier;
            _logger = logManager.GetClassLogger();
            _timer = new Timer(tryClaimRefundsIntervalMilliseconds);
        }

        public void Init()
        {
            _timer.Start();
            _timer.Elapsed += TimerOnElapsed;
            _blockProcessor.BlockProcessed += OnBlockProcessed;
            if (_logger.IsInfo) _logger.Info("Initialized NDM consumer services background processor.");
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

                    var refundTo = _accountService.GetAddress();
                    foreach (var deposit in t.Result.Items)
                    {
                        await _refundClaimant.TryClaimEarlyRefundAsync(deposit, refundTo);
                        await _refundClaimant.TryClaimRefundAsync(deposit, refundTo);
                    }
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