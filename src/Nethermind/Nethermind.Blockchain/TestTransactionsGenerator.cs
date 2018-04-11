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
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    public class TestTransactionsGenerator
    {
        private readonly ILogger _logger;
        private readonly PrivateKey _privateKey;
        private readonly byte[] _privateKeyBytes = new byte[32];
        private readonly Random _random = new Random();
        private readonly IEthereumSigner _signer;
        private readonly ITransactionStore _store;
        private readonly Timer _timer = new Timer();

        private int _count;

        public TestTransactionsGenerator(ITransactionStore store, IEthereumSigner signer, TimeSpan txDelay, ILogger logger)
        {
            _store = store;
            _signer = signer;
            _logger = logger;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Interval = txDelay.TotalMilliseconds;

            _random.NextBytes(_privateKeyBytes);
            _privateKey = new PrivateKey(_privateKeyBytes);
            SenderAddress = _privateKey.PublicKey.Address;
            _logger.Debug($"Test transactions will be coming from {SenderAddress}.");
        }

        public Address SenderAddress { get; }

        private void TimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            _logger.Debug($"Generating a test transaction for testing ({_count}).");

            Transaction tx = new Transaction();
            tx.GasPrice = 1;
            tx.GasLimit = 21000;
            tx.To = new Address(0x0f.ToBigEndianByteArray().PadLeft(20));
            tx.Nonce = 0;
            tx.Value = 1;
            tx.Data = new byte[0];
            tx.Nonce = _count++;
            _signer.Sign(_privateKey, tx, 0);
            Address address = _signer.RecoverAddress(tx, 0);
            if (address != SenderAddress)
            {
                _logger.Debug($"Signature mismatch in tests generator (EIP?).");
            }

            tx.Hash = Transaction.CalculateHash(tx);

            _store.AddPending(tx);
            _logger.Debug($"Generated a test transaction for testing ({_count - 1}).");
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