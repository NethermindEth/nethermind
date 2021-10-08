using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    [TestFixture]
    public class DepositDetailsTests
    {
        private DepositDetails _depositDetails;
        private Deposit _deposit; 
        private DataAsset _dataAsset;
        private Address _consumerAddress;


        [SetUp]
        public void SetUp()
        {
            _consumerAddress = TestItem.AddressA;

            _deposit = new Deposit(id: Keccak.Zero,
                                   units: 10,
                                   expiryTime: 20,
                                   value: 1);

            _depositDetails = new DepositDetails(_deposit,
                                                 _dataAsset,
                                                 _consumerAddress,
                                                 pepper: new byte[] { 1, 2, 3}, 
                                                 timestamp: 10, 
                                                 transactions: null);
        }

        [Test]
        public void when_is_expired_returns_true()
        {
            var isExpired = _depositDetails.IsExpired(30);
            Assert.IsTrue(isExpired);
        }

        [Test]
        public void when_is_not_expired_returns_false()
        {
            var isExpired = _depositDetails.IsExpired(19);
            Assert.IsFalse(isExpired);
        }

        [Test]
        public void will_return_true_when_timestamp_is_equal_to_deposit_timestamp()
        {
            var isExpired = _depositDetails.IsExpired(20);
            Assert.IsTrue(isExpired);
        }

        [Test]
        public void will_set_confirmation_timestamp()
        {
            _depositDetails.SetConfirmationTimestamp(30);
            Assert.AreEqual(_depositDetails.ConfirmationTimestamp, 30);
        }

        [Test]
        public void will_not_set_confirmation_timestamp_when_given_0()
        {
            Assert.Throws<InvalidOperationException>(() => _depositDetails.SetConfirmationTimestamp(0));
        }
        
        [Test]
        public void can_add_transaction()
        {
            var transaction = new TransactionInfo(Keccak.OfAnEmptyString, 
            value: 10, 
            gasPrice: 1, 
            gasLimit: 20,
            timestamp: 30
            );

            Assert.DoesNotThrow(() => _depositDetails.AddTransaction(transaction));

            var addedTransaction = _depositDetails.Transactions.Single(t => t.Hash.Equals(Keccak.OfAnEmptyString));

            Assert.AreEqual(transaction, addedTransaction);
        }

        [Test]
        public void can_include_transaction()
        {
            var transaction = new TransactionInfo(Keccak.OfAnEmptyString, 
            value: 10, 
            gasPrice: 1, 
            gasLimit: 20,
            timestamp: 30
            );

            var transaction2 = new TransactionInfo(Keccak.OfAnEmptySequenceRlp, 
            value: 10, 
            gasPrice: 1, 
            gasLimit: 20,
            timestamp: 30
            );

            _depositDetails.AddTransaction(transaction);
            _depositDetails.AddTransaction(transaction2);
            _depositDetails.SetIncludedTransaction(transaction.Hash);

            var includedTransaction = _depositDetails.Transaction;
            var rejectedTransaction = _depositDetails.Transactions.Single(t => t.Hash == transaction2.Hash);

            Assert.IsTrue(includedTransaction.State == TransactionState.Included);
            Assert.IsTrue(rejectedTransaction.State == TransactionState.Rejected);
        } 

        [Test]
        public void can_set_rejected()
        {
            _depositDetails.Reject();
            Assert.IsTrue(_depositDetails.Rejected);
        }

        [Test]
        public void can_set_early_refund_ticket()
        {
            var earlyRefundTicket = new EarlyRefundTicket(Keccak.Zero,
                                                        claimableAfter: 10, 
                                                        new Signature(1, 2, 37));

            _depositDetails.SetEarlyRefundTicket(earlyRefundTicket);
            Assert.AreEqual(earlyRefundTicket, _depositDetails.EarlyRefundTicket);
        }

        [Test]
        public void can_add_claimed_refund_transaction()
        {
            var transaction = new TransactionInfo(Keccak.Zero, 
                                                value: 10, 
                                                gasPrice: 1,
                                                gasLimit: 20,
                                                timestamp: 10
                                                );
            
            _depositDetails.AddClaimedRefundTransaction(transaction);

            Assert.AreEqual(transaction, _depositDetails.ClaimedRefundTransaction);
        }

        [Test]
        public void can_set_included_on_claimed_refund()
        {
            var transaction = new TransactionInfo(Keccak.Zero, 
                                                value: 10, 
                                                gasPrice: 1,
                                                gasLimit: 20,
                                                timestamp: 10
                                                );
            
            _depositDetails.AddClaimedRefundTransaction(transaction);

            _depositDetails.SetIncludedClaimedRefundTransaction(transaction.Hash);

            Assert.IsTrue(_depositDetails.ClaimedRefundTransaction.State == TransactionState.Included);
        }

        [Test]
        public void can_set_refund_claimed()
        {
            _depositDetails.SetRefundClaimed();
            Assert.IsTrue(_depositDetails.RefundClaimed);
        }

        [Test]
        public void can_set_consumed_units()
        {
            _depositDetails.SetConsumedUnits(100);
            Assert.IsTrue(_depositDetails.ConsumedUnits == 100);
        }

        [Test]
        public void can_claim_early_refund_returns_correctly_true()
        {
            var earlyRefundTicket = new EarlyRefundTicket(Keccak.Zero,
                                                        claimableAfter: 10, 
                                                        new Signature(1, 2, 37));

            _depositDetails.SetEarlyRefundTicket(earlyRefundTicket);
            _depositDetails.SetConfirmationTimestamp(10);

            bool canClaimEarlyRefund = _depositDetails.CanClaimEarlyRefund(currentBlockTimestamp: 20, depositTimestamp: 5);

            Assert.IsTrue(canClaimEarlyRefund);
        }

        [Test]
        public void will_not_set_can_claim_early_refund_when_current_timestamp_is_lower_than_deposit_timestamp()
        {
            var earlyRefundTicket = new EarlyRefundTicket(Keccak.Zero,
                                                        claimableAfter: 10, 
                                                        new Signature(1, 2, 37));

            _depositDetails.SetEarlyRefundTicket(earlyRefundTicket);
            _depositDetails.SetConfirmationTimestamp(10);

            bool canClaimEarlyRefund = _depositDetails.CanClaimEarlyRefund(currentBlockTimestamp: 10, depositTimestamp: 50);

            Assert.IsFalse(canClaimEarlyRefund);
        }

        [Test]
        public void can_claim_refund_returns_correctly_true()
        {
            _depositDetails.SetConfirmationTimestamp(10);

            bool canClaimRefund = _depositDetails.CanClaimRefund(30);

            Assert.IsTrue(canClaimRefund);
        }

        [Test]
        public void will_not_set_can_claim_refund_when_timestamp_is_lower_than_deposit_timestamp()
        {
            _depositDetails.SetConfirmationTimestamp(10);

            bool canClaimRefund = _depositDetails.CanClaimRefund(1);

            Assert.IsFalse(canClaimRefund);
        }

        [Test]
        public void returns_time_left_to_claim_refund_correctly()
        {
            _depositDetails.SetConfirmationTimestamp(5);

            UInt256 timeLeft = _depositDetails.GetTimeLeftToClaimRefund(8);

            Assert.IsTrue(timeLeft == 12); 
        }

        [Test]
        public void should_throw_when_timestamp_is_bigger_than_deposit_timestamp()
        {
            _depositDetails.SetConfirmationTimestamp(5);

            var timeLeft = _depositDetails.GetTimeLeftToClaimRefund(30);

            Assert.IsTrue(timeLeft == 0);
        }

        [Test]
        public void equals_returns_correctly_true()
        {
            var depositDetailsClone = _depositDetails;
            
            bool equals = depositDetailsClone.Equals(_depositDetails);

            Assert.IsTrue(equals);
        }

        [Test]
        public void equals_returns_correctly_false()
        {
            var deposit = new Deposit(Keccak.OfAnEmptyString, 10, 10, 10);
            var depositDetails = new DepositDetails(deposit,
                                                 _dataAsset,
                                                 _consumerAddress,
                                                 pepper: new byte[] { 5, 8, 6}, 
                                                 timestamp: 50, 
                                                 transactions: null);
            
            bool equals = depositDetails.Equals(_depositDetails);

            Assert.IsFalse(equals);
        }
    }
}