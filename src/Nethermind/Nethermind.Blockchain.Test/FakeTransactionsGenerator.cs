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
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Blockchain.Test
{
    internal class FakeTransactionsGenerator
    {
        private readonly ITransactionStore _store;
        private readonly IEthereumSigner _signer;
        private readonly ILogger _logger;
        private readonly Timer _timer = new Timer();
        private readonly Random _random = new Random();

        private int _count;
        
        public FakeTransactionsGenerator(ITransactionStore store, IEthereumSigner signer, TimeSpan txDelay, ILogger logger)
        {
            _store = store;
            _signer = signer;
            _logger = logger;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Interval = txDelay.TotalMilliseconds;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            _logger.Debug($"Generating a fake transaction for testing ({_count}).");
            byte[] privateKeyBytes = new byte[32];
            _random.NextBytes(privateKeyBytes);
            PrivateKey privateKey = new PrivateKey(privateKeyBytes);
            _store.AddPending(Build.A.Transaction.Signed(_signer, privateKey).TestObject);
            _logger.Debug($"Generated a fake transaction for testing ({_count}).");
            _count++;
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }
    }
}