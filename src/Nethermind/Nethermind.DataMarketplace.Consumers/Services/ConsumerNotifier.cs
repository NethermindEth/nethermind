using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public class ConsumerNotifier : IConsumerNotifier
    {
        private readonly INdmNotifier _notifier;

        public ConsumerNotifier(INdmNotifier notifier)
        {
            _notifier = notifier;
        }

        public Task SendDepositConfirmationsStatusAsync(Keccak depositId, int confirmations, int requiredConfirmations)
            => _notifier.NotifyAsync(new Notification("deposit_confirmations",
                new
                {
                    depositId,
                    confirmations,
                    requiredConfirmations
                }));

        public Task SendInvalidDataReasonAsync(Keccak depositId, InvalidDataReason reason)
            => _notifier.NotifyAsync(new Notification("invalid_data",
                new
                {
                    depositId,
                    reason = reason.ToString()
                }));
    }
}