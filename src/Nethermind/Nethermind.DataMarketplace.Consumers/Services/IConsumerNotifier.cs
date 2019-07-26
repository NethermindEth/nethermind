using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public interface IConsumerNotifier
    {
        Task SendDepositConfirmationsStatusAsync(Keccak depositId, uint confirmations, uint requiredConfirmations,
            uint verificationTimestamp);

        Task SendDataInvalidAsync(Keccak depositId, InvalidDataReason reason);
    }
}