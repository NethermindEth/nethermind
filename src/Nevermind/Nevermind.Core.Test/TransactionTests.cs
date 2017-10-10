using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class TransactionTests
    {
        [Test]
        public void When_init_and_data_empty_then_is_transfer()
        {
            Transaction transaction = new Transaction();
            transaction.Init = null;
            transaction.Data = null;
            Assert.True(transaction.IsTransfer, nameof(Transaction.IsTransfer));
            Assert.False(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.False(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [Test]
        public void When_init_empty_and_data_not_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.Init = null;
            transaction.Data = new byte[0];
            Assert.False(transaction.IsTransfer, nameof(Transaction.IsTransfer));
            Assert.True(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.False(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [Test]
        public void When_init_not_empty_and_data_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.Init = new byte[0];
            transaction.Data = null;
            Assert.False(transaction.IsTransfer, nameof(Transaction.IsTransfer));
            Assert.False(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.True(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }
    }
}