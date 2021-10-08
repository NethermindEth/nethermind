using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class InstantPaymentClaimProcessor : IPaymentClaimProcessor
    {
        private readonly IPaymentClaimProcessor _processor;
        private readonly IPaymentClaimRepository _repository;
        private readonly ILogger _logger;

        public InstantPaymentClaimProcessor(IPaymentClaimProcessor processor, IPaymentClaimRepository repository,
            ILogManager logManager)
        {
            _processor = processor;
            _repository = repository;
            _logger = logManager.GetClassLogger();
        }

        public async Task<PaymentClaim?> ProcessAsync(DataDeliveryReceiptRequest receiptRequest, Signature signature)
        {
            var depositId = receiptRequest.DepositId;
            if (_logger.IsWarn) _logger.Warn($"NDM provider instantly verifying payment claim for deposit: '{depositId}'...");
            var paymentClaim = await _processor.ProcessAsync(receiptRequest, signature);
            if (paymentClaim is null)
            {
                return null;
            }
            
            paymentClaim.SetTransactionCost(0);
            await _repository.UpdateAsync(paymentClaim);
            if (_logger.IsWarn) _logger.Warn($"NDM provider instantly verified payment claim (id: '{paymentClaim.Id}') for deposit: '{depositId}'.");

            return paymentClaim;
        }

        public Task<Keccak?> SendTransactionAsync(PaymentClaim paymentClaim, UInt256 gasPrice)
            => _processor.SendTransactionAsync(paymentClaim, gasPrice);


        public void ChangeColdWalletAddress(Address address)
            => _processor.ChangeColdWalletAddress(address);
    }
}