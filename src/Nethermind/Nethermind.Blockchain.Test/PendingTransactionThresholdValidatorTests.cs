using System;
using FluentAssertions;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class PendingTransactionThresholdValidatorTests
    {
        private readonly ITransactionPoolTimer _timer = new TransactionPoolTimer();
        private int _obsoletePendingTransactionInterval;
        private int _removePendingTransactionInterval;

        [Test]
        public void should_mark_transaction_as_obsolete()
        {
            var validator = GetValidator();
            var transaction1 = GetTransaction(5);
            var transaction2 = GetTransaction(15);
            var transaction3 = GetTransaction(25);
            var currentTimestamp = _timer.CurrentTimestamp;
            validator.IsObsolete(currentTimestamp, transaction1.Timestamp).Should().BeFalse();
            validator.IsObsolete(currentTimestamp, transaction2.Timestamp).Should().BeFalse();
            validator.IsObsolete(currentTimestamp, transaction3.Timestamp).Should().BeTrue();
        }

        [Test]
        public void should_mark_transaction_as_removable()
        {
            var validator = GetValidator();
            var transaction1 = GetTransaction(5);
            var transaction2 = GetTransaction(600);
            var transaction3 = GetTransaction(1000);
            var currentTimestamp = _timer.CurrentTimestamp;
            validator.IsObsolete(currentTimestamp, transaction1.Timestamp).Should().BeFalse();
            validator.IsRemovable(currentTimestamp, transaction2.Timestamp).Should().BeFalse();
            validator.IsRemovable(currentTimestamp, transaction3.Timestamp).Should().BeTrue();
        }
        
        private PendingTransactionThresholdValidator GetValidator(int obsoletePendingTransactionInterval = 15,
            int removePendingTransactionInterval = 600)
        {
            _obsoletePendingTransactionInterval = obsoletePendingTransactionInterval;
            _removePendingTransactionInterval = removePendingTransactionInterval;

            return new PendingTransactionThresholdValidator(_obsoletePendingTransactionInterval,
                _removePendingTransactionInterval);
        }

        private Transaction GetTransaction(int createdSecondsAgo = 0)
            => new Transaction
            {
                Timestamp = GetTransactionTimestamp(createdSecondsAgo)
            };

        private static UInt256 GetTransactionTimestamp(int createdSecondsAgo = 0)
            => new UInt256(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() - createdSecondsAgo);
    }
}