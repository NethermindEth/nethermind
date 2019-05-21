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
using System.IO;
using System.Timers;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Test
{
    public class TestTransactionsGenerator
    {
        private readonly ILogger _logger;
        private readonly PrivateKey _privateKey;
        private readonly byte[] _privateKeyBytes = new byte[32];
        private readonly Random _random = new Random();
        private readonly IEthereumEcdsa _ecdsa;
        private readonly TimeSpan _txDelay;
        private readonly ITxPool _txPool;
        private readonly Timer _timer = new Timer();

        private ulong _count;
        
        private TimeSpan RandomizeDelay()
        {
            return _txDelay + TimeSpan.FromMilliseconds((_random.Next((int)_txDelay.TotalMilliseconds) - (int)_txDelay.TotalMilliseconds / 2));
        }
        
        public TestTransactionsGenerator(ITxPool txPool, IEthereumEcdsa ecdsa, TimeSpan txDelay, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txDelay = txDelay;

            if (txDelay > TimeSpan.FromMilliseconds(0))
            {
                _timer.Elapsed += TimerOnElapsed;
                _timer.Interval = txDelay.TotalMilliseconds;    
            }

            _privateKeyBytes[31] = 1;
            _privateKey = new PrivateKey(_privateKeyBytes);
            SenderAddress = _privateKey.PublicKey.Address;
            _logger.Debug($"Test transactions will be coming from {SenderAddress}.");
        }

        public Address SenderAddress { get; }

        private UInt256 _nonce = 0;
        
        private void TimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            _timer.Interval = RandomizeDelay().TotalMilliseconds;
            _logger.Debug($"Generating a test transaction for testing ({_count}).");

            Transaction tx = new Transaction();
            tx.GasPrice = 1;
            tx.GasLimit = 21000;
            tx.To = new Address(0x0f.ToBigEndianByteArray().PadLeft(20));
            tx.Nonce = _nonce++;
            tx.Value = 1;
            tx.Data = new byte[0];
            tx.Nonce = _count++;
            tx.SenderAddress = SenderAddress;
            _ecdsa.Sign(_privateKey, tx, 1);
            Address address = _ecdsa.RecoverAddress(tx, 1);
            if (address != tx.SenderAddress)
            {
                throw new InvalidDataException($"{nameof(TestTransactionsGenerator)} producing invalid transactions");
            }

            tx.Hash = Transaction.CalculateHash(tx);

            _txPool.AddTransaction(tx, 1);
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