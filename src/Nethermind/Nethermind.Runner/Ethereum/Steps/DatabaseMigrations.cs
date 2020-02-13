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

using System.Diagnostics;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Bloom;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain))]
    public class DatabaseMigrations : IStep
    {
        private readonly EthereumRunnerContext _context;
        private readonly ILogger _logger;
        private Stopwatch _stopwatch;
        private long _migrated = 0;
        private long _toMigrate;

        public DatabaseMigrations(EthereumRunnerContext context)
        {
            _context = context;
            _logger = context.LogManager.GetClassLogger();
        }

        public bool MustInitialize => false;

        public Task Execute()
        {
            // StartBloomMigration();
            return Task.CompletedTask;
        }

        private void StartBloomMigration()
        {
            var storage = _context.BloomStorage;
            if (storage.NeedsMigration)
            {
                _toMigrate = MinBlockNumber;
                _stopwatch = Stopwatch.StartNew();
                // RunBloomMigration();
                // Task.Run(RunBloomMigration)
                //     .ContinueWith(x =>
                //     {
                //         if (x.IsFaulted && _logger.IsError)
                //         {
                //             _stopwatch.Stop();
                //             _logger.Error(GetLogMessage("failed", $"Error: {x.Exception}"), x.Exception);
                //         }
                //     });
            }
        }

        private long MinBlockNumber => _context.BloomStorage.MinBlockNumber == long.MaxValue
            ? _context.BlockTree.BestKnownNumber
            : _context.BloomStorage.MinBlockNumber;
        
        private void RunBloomMigration()
        {
            if (_logger.IsInfo) _logger.Info(GetLogMessage("started"));
            
            // using var timer = new Timer() {Interval = 1000};
            // timer.Elapsed += (ElapsedEventHandler)((o, e) =>
            // {
            //     if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
            // });

            var storage = _context.BloomStorage;
            var blockTree = _context.BlockTree;
            for (long i = MinBlockNumber; i >= 0; i--)
            {
                var header = blockTree.FindHeader(i);
                if (header != null)
                {
                    storage.Store(i, header.Bloom);
                }

                _migrated++;

                if (i % 1000 == 0)
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
                }
            }

            _stopwatch.Stop();
            if (_logger.IsInfo) _logger.Info(GetLogMessage("finished"));
        }

        private string GetLogMessage(string status, string suffix = null) => $"{_stopwatch.Elapsed:g} | BloomDb migration {status}. {_migrated} / {_toMigrate} blocks migrated. {suffix}";
    }
}