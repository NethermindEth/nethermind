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
using FluentAssertions;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class PendingTransactionThresholdValidatorTests
    {
        private int _obsoletePendingTransactionInterval;
        private int _removePendingTransactionInterval;

        [Test]
        public void should_mark_transaction_as_obsolete()
        {
            var utcNow = DateTime.UtcNow;
            var validator = GetValidator();
            var timestamp = new Timestamper(utcNow);
            var transaction1 = GetTransaction(utcNow, 5);
            var transaction2 = GetTransaction(utcNow, 15);
            var transaction3 = GetTransaction(utcNow, 25);
            var currentTimestamp = new UInt256(timestamp.EpochSeconds);
            validator.IsObsolete(currentTimestamp, transaction1.Timestamp).Should().BeFalse();
            validator.IsObsolete(currentTimestamp, transaction2.Timestamp).Should().BeFalse();
            validator.IsObsolete(currentTimestamp, transaction3.Timestamp).Should().BeTrue();
        }

        [Test]
        public void should_mark_transaction_as_removable()
        {
            var utcNow = DateTime.UtcNow;
            var validator = GetValidator();
            var timestamp = new Timestamper(utcNow);
            var transaction1 = GetTransaction(utcNow, 5);
            var transaction2 = GetTransaction(utcNow, 600);
            var transaction3 = GetTransaction(utcNow, 1000);
            var currentTimestamp = new UInt256(timestamp.EpochSeconds);
            validator.IsRemovable(currentTimestamp, transaction1.Timestamp).Should().BeFalse();
            validator.IsRemovable(currentTimestamp, transaction2.Timestamp).Should().BeFalse();
            validator.IsRemovable(currentTimestamp, transaction3.Timestamp).Should().BeTrue();
        }

        private PendingTxThresholdValidator GetValidator(int obsoletePendingTransactionInterval = 15,
            int removePendingTransactionInterval = 600)
        {
            _obsoletePendingTransactionInterval = obsoletePendingTransactionInterval;
            _removePendingTransactionInterval = removePendingTransactionInterval;

            TxPoolConfig config = new TxPoolConfig
            {
                ObsoletePendingTransactionInterval = _obsoletePendingTransactionInterval,
                RemovePendingTransactionInterval = _removePendingTransactionInterval
            };
            
            return new PendingTxThresholdValidator(config);
        }

        private Transaction GetTransaction(DateTime utcNow, int createdSecondsAgo = 0)
            => new Transaction
            {
                Timestamp = GetTransactionTimestamp(utcNow, createdSecondsAgo)
            };

        private static UInt256 GetTransactionTimestamp(DateTime utcNow, int createdSecondsAgo = 0)
            => new UInt256(new DateTimeOffset(utcNow).ToUnixTimeSeconds() - createdSecondsAgo);
    }
}