using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Services
{
    internal class ProviderTransactionsService : IProviderTransactionsService
    {
        private readonly ITransactionService _transactionService;
        private readonly IPaymentClaimRepository _paymentClaimRepository;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        public ProviderTransactionsService(ITransactionService transactionService,
            IPaymentClaimRepository paymentClaimRepository, ITimestamper timestamper, ILogManager logManager)
        {
            _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            _paymentClaimRepository = paymentClaimRepository ?? throw new ArgumentNullException(nameof(paymentClaimRepository));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<IEnumerable<ResourceTransaction>> GetPendingAsync()
        {
            var paymentClaims = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
            {
                OnlyPending = true,
                Results = int.MaxValue
            });

            return paymentClaims.Items.Where(pc => pc != null).Select(c => new ResourceTransaction(c!.Id.ToString(), "payment",
                c!.Transaction!));
        }
        
        public async Task<IEnumerable<ResourceTransaction>> GetAllTransactionsAsync()
        {
            var paymentClaims = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
            {
                Results = int.MaxValue
            });

            return paymentClaims.Items.Where(pc => pc != null).Select(c => new ResourceTransaction(c!.Id.ToString(), "payment",
                c!.Transaction!));
        }

        public async Task<UpdatedTransactionInfo> UpdatePaymentClaimGasPriceAsync(Keccak paymentClaimId, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                if (_logger.IsError) _logger.Error("Gas price cannot be 0.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.InvalidGasPrice);
            }

            (UpdatedTransactionStatus status, PaymentClaim? paymentClaim) = await TryGetPaymentClaimAsync(paymentClaimId, "update gas price");
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }
            
            TransactionInfo? currentTransaction = paymentClaim!.Transaction;
            if (currentTransaction == null)
            {
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            Keccak? currentHash = currentTransaction.Hash;
            if (currentHash == null)
            {
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            ulong gasLimit = currentTransaction.GasLimit;
            if (_logger.IsInfo) _logger.Info($"Updating gas price for payment claim with id: '{paymentClaim!.Id}', current transaction hash: '{currentHash}'.");
            Keccak transactionHash = await _transactionService.UpdateGasPriceAsync(currentHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for payment claim with id: '{paymentClaim.Id}' after updating gas price.");
            paymentClaim!.AddTransaction(TransactionInfo.SpeedUp(transactionHash, 0, gasPrice, gasLimit,
                _timestamper.UnixTime.Seconds));
            await _paymentClaimRepository.UpdateAsync(paymentClaim!);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for payment claim with id: '{paymentClaim!.Id}', transaction hash: '{transactionHash}'.");

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transactionHash);
        }

        public async Task<UpdatedTransactionInfo> CancelPaymentClaimAsync(Keccak paymentClaimId)
        {
            (UpdatedTransactionStatus status, PaymentClaim? paymentClaim) = await TryGetPaymentClaimAsync(paymentClaimId, "cancel");
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }

            TransactionInfo? currentTransaction = paymentClaim!.Transaction; 
            if (currentTransaction == null)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel missing transaction for payment claim with id: '{paymentClaim!.Id}'.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }

            Keccak? hash = currentTransaction.Hash;
            if (hash == null)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel missing transaction for payment claim with id: '{paymentClaim!.Id}'.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }

            if (currentTransaction.State != TransactionState.Pending)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel transaction with hash: '{hash}' for payment claim with id: '{paymentClaim!.Id}' (state: '{currentTransaction.State}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.AlreadyIncluded);
            }
            
            if (_logger.IsWarn) _logger.Warn($"Canceling transaction for payment claim with id: '{paymentClaimId}'.");
            CanceledTransactionInfo transaction = await _transactionService.CancelAsync(hash);
            if (_logger.IsWarn) _logger.Warn($"Canceled transaction for payment claim with id: '{paymentClaimId}', transaction hash: '{transaction.Hash}'.");
            TransactionInfo cancellingTransaction = TransactionInfo.Cancellation(transaction.Hash, transaction.GasPrice,
                transaction.GasLimit, _timestamper.UnixTime.Seconds);
            paymentClaim!.AddTransaction(cancellingTransaction);
            await _paymentClaimRepository.UpdateAsync(paymentClaim!);

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transaction.Hash);
        }
        
        private async Task<(UpdatedTransactionStatus status, PaymentClaim?)> TryGetPaymentClaimAsync(Keccak paymentClaimId, string method)
        {
            PaymentClaim paymentClaim = await _paymentClaimRepository.GetAsync(paymentClaimId);
            if (paymentClaim is null)
            {
                if (_logger.IsError) _logger.Error($"Payment claim with id: '{paymentClaimId}' was not found.");
                return (UpdatedTransactionStatus.ResourceNotFound, null);
            }
            
            if (paymentClaim.Transaction is null)
            {
                if (_logger.IsError) _logger.Error($"Payment claim with id: '{paymentClaimId}' has no transaction.");
                return (UpdatedTransactionStatus.MissingTransaction, null);
            }
            
            switch (paymentClaim.Status)
            {
                case PaymentClaimStatus.Sent:
                    
                    return (UpdatedTransactionStatus.Ok, paymentClaim);
                case PaymentClaimStatus.Rejected:
                    if (_logger.IsError) LogError();
                    return (UpdatedTransactionStatus.ResourceRejected, null);
                case PaymentClaimStatus.Cancelled:
                    if (_logger.IsError) LogError();
                    return (UpdatedTransactionStatus.ResourceCancelled, null);
                default:
                    if (_logger.IsError) LogError();
                    return (UpdatedTransactionStatus.AlreadyIncluded, null);
                
                void LogError() => _logger.Error($"Cannot {method} for payment claim with id: '{paymentClaimId}' (status: '{paymentClaim.Status}').");
            }
        }
    }
}