using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public interface IConsumerNotifier
    {
        Task SendDepositConfirmationsStatusAsync(Keccak depositId, int confirmations, int requiredConfirmations);
    }
}