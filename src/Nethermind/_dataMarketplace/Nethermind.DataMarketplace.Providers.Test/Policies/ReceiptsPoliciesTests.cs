using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Policies
{
    internal class ReceiptsPoliciesTests
    {
        private UInt256 _receiptRequestThreshold;
        private UInt256 _receiptsMergeThreshold;
        private UInt256 _paymentClaimThreshold;
        private UInt256 _unitPrice;
        private long _sessionUnpaidUnits;
        private long _consumerUnpaidUnits;
        private IProviderThresholdsService _providerThresholdsService;

        [SetUp]
        public void Setup()
        {
            _receiptRequestThreshold = 10000000000000000;
            _receiptsMergeThreshold = 100000000000000000;
            _paymentClaimThreshold = 1000000000000000000;
            _unitPrice = 100000000000000;
            _providerThresholdsService = Substitute.For<IProviderThresholdsService>();
        }
        
        [Test]
        public async Task given_unpaid_value_lower_than_receipt_request_policy_it_should_not_be_possible_to_send_receipt()
        {
            _providerThresholdsService.GetCurrentReceiptRequestAsync().Returns(_receiptRequestThreshold);
            _sessionUnpaidUnits = 99;
            var policies = CreatePolicies();
            var result = await policies.CanRequestReceipts(_sessionUnpaidUnits, _unitPrice);
            result.Should().BeFalse();
        }
        
        [Test]
        public async Task given_unpaid_value_equal_to_receipt_request_policy_it_should_be_possible_to_send_receipt()
        {
            _providerThresholdsService.GetCurrentReceiptRequestAsync().Returns(_receiptRequestThreshold);
            _sessionUnpaidUnits = 100;
            var policies = CreatePolicies();
            var result = await policies.CanRequestReceipts(_sessionUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
        
        [Test]
        public async Task given_unpaid_value_greater_than_receipt_request_policy_it_should_be_possible_to_send_receipt()
        {
            _providerThresholdsService.GetCurrentReceiptRequestAsync().Returns(_receiptRequestThreshold);
            _sessionUnpaidUnits = 101;
            var policies = CreatePolicies();
            var result = await policies.CanRequestReceipts(_sessionUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
        
        [Test]
        public async Task given_unpaid_value_lower_than_merge_receipts_policy_it_should_not_be_possible_to_merge_receipts()
        {
            _providerThresholdsService.GetCurrentReceiptsMergeAsync().Returns(_receiptsMergeThreshold);
            _consumerUnpaidUnits = 999;
            var policies = CreatePolicies();
            var result = await policies.CanMergeReceipts(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeFalse();
        }
        

        [Test]
        public async Task given_unpaid_value_equal_to_merge_receipts_policy_it_should_be_possible_to_merge_receipts()
        {
            _providerThresholdsService.GetCurrentReceiptsMergeAsync().Returns(_receiptsMergeThreshold);
            _consumerUnpaidUnits = 1000;
            var policies = CreatePolicies();
            var result = await policies.CanMergeReceipts(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
        
        [Test]
        public async Task given_unpaid_value_greater_than_merge_receipts_policy_it_should_be_possible_to_merge_receipts()
        {
            _providerThresholdsService.GetCurrentReceiptsMergeAsync().Returns(_receiptsMergeThreshold);
            _consumerUnpaidUnits = 1001;
            var policies = CreatePolicies();
            var result = await policies.CanMergeReceipts(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
        
        [Test]
        public async Task given_unpaid_value_lower_than_claim_payment_policy_it_should_not_be_possible_to_send_claim()
        {
            _providerThresholdsService.GetCurrentPaymentClaimAsync().Returns(_paymentClaimThreshold);
            _consumerUnpaidUnits = 9990;
            var policies = CreatePolicies();
            var result = await policies.CanClaimPayment(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeFalse();
        }

        [Test]
        public async Task given_unpaid_value_equal_to_claim_payment_policy_it_should_be_possible_to_send_claim()
        {
            _providerThresholdsService.GetCurrentPaymentClaimAsync().Returns(_paymentClaimThreshold);
            _consumerUnpaidUnits = 10000;
            var policies = CreatePolicies();
            var result = await policies.CanClaimPayment(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
        
        [Test]
        public async Task given_unpaid_value_greater_than_claim_payment_policy_it_should_be_possible_to_send_claim()
        {
            await _providerThresholdsService.SetPaymentClaimAsync(_paymentClaimThreshold);
            _consumerUnpaidUnits = 10001;
            var policies = CreatePolicies();
            var result = await policies.CanClaimPayment(_consumerUnpaidUnits, _unitPrice);
            result.Should().BeTrue();
        }
       
        private IReceiptsPolicies CreatePolicies()
            => new ReceiptsPolicies(_providerThresholdsService);
    }
}