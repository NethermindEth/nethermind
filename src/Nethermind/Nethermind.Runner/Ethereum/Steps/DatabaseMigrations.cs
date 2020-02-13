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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Timers;
using Microsoft.Extensions.Logging;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Bloom;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Runner.Ethereum.Context;
using ILogger = Nethermind.Logging.ILogger;
using Timer = System.Timers.Timer;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain))]
    public class DatabaseMigrations : IStep, IAsyncDisposable
    {
        private readonly EthereumRunnerContext _context;
        private readonly ILogger _logger;
        private Stopwatch _stopwatch;
        private readonly MeasuredProgress _progress = new MeasuredProgress(); 
        private long _toMigrate;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _bloomDbMigrationTask;

        public DatabaseMigrations(EthereumRunnerContext context)
        {
            _context = context;
            _logger = context.LogManager.GetClassLogger();
        }

        public bool MustInitialize => false;

        public Task Execute()
        {
            StartBloomMigration();
            return Task.CompletedTask;
        }

        private void StartBloomMigration()
        {
            var storage = _context.BloomStorage;
            if (storage.NeedsMigration)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _context.DisposeStack.Push(this);
                _stopwatch = Stopwatch.StartNew();
                _bloomDbMigrationTask = Task.Run(() => RunBloomMigration(_cancellationTokenSource.Token))
                    .ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsError)
                        {
                            _stopwatch.Stop();
                            _logger.Error(GetLogMessage("failed", $"Error: {x.Exception}"), x.Exception);
                        }
                    });
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("Skipping BloomDb migration.");
            }
        }

        private long MinBlockNumber => _context.BloomStorage.MinBlockNumber == long.MaxValue
            ? _context.BlockTree.BestKnownNumber
            : _context.BloomStorage.MinBlockNumber;

        private void RunBloomMigration(CancellationToken token)
        {
            var minBlockNumber = MinBlockNumber;
            _toMigrate = MinBlockNumber + 1;
            
            if (_logger.IsInfo) _logger.Info(GetLogMessage("started"));

            using (var timer = new Timer(1000) {Enabled = true})
            {
                timer.Elapsed += (ElapsedEventHandler) ((o, e) =>
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
                });

                var storage = _context.BloomStorage;
                var concurrentStorage = storage as IConcurrentStorage<long>;
                var blockTree = _context.BlockTree;

                concurrentStorage?.StartConcurrent(minBlockNumber);
                long synced = 0;
                try
                {
                    for (long i = minBlockNumber; i >= 0; i--)
                    {
                        if (token.IsCancellationRequested)
                        {
                            timer.Stop();
                            if (_logger.IsInfo) _logger.Info(GetLogMessage("cancelled"));
                            return;
                        }

                        var header = blockTree.FindHeader(i);
                        if (header != null)
                        {
                            storage.Store(i, header.Bloom);
                        }

                        _progress.Update(++synced);
                    }

                    _progress.MarkEnd();
                }
                finally
                {
                    concurrentStorage?.EndConcurrent(minBlockNumber);
                    _stopwatch.Stop();
                }
            }

            if (_logger.IsInfo) _logger.Info(GetLogMessage("finished"));
        }

        private string GetLogMessage(string status, string suffix = null)
        {
            var message = $"BloomDb migration {status} | {_stopwatch.Elapsed:d\\:hh\\:mm\\:ss} | {_progress.CurrentValue.ToString().PadLeft(_toMigrate.ToString().Length)} / {_toMigrate} blocks migrated. | current {_progress.CurrentPerSecond:F2}bps | total {_progress.TotalPerSecond:F2}bps. {suffix}";
            _progress.SetMeasuringPoint();
            return message;

        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource.Cancel();
            await _bloomDbMigrationTask;
        }
    }
}