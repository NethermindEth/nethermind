using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public interface IConsumerNotifier
    {
        Task SendDataRequestResultAsync(Keccak depositId, DataRequestResult result);

        Task SendDepositConfirmationsStatusAsync(Keccak depositId, string dataAssetName, uint confirmations,
            uint requiredConfirmations, uint confirmationTimestamp, bool confirmed);

        Task SendDataInvalidAsync(Keccak depositId, InvalidDataReason reason);
        Task SendSessionStartedAsync(Keccak depositId, Keccak sessionId);
        Task SendSessionFinishedAsync(Keccak depositId, Keccak sessionId);
        Task SendConsumerAccountLockedAsync(Address address);
        Task SendConsumerAddressChangedAsync(Address newAddress, Address previousAddress);
        Task SendProviderAddressChangedAsync(Address newAddress, Address previousAddress);
        Task SendDataAssetStateChangedAsync(Keccak id, string name, DataHeaderState state);
        Task SendDataAssetRemovedAsync(Keccak id, string name);
        Task SendDataAvailabilityChangedAsync(Keccak depositId, Keccak sessionId, DataAvailability availability);
        Task SendDataStreamEnabledAsync(Keccak depositId, Keccak sessionId);
        Task SendDataStreamDisabledAsync(Keccak depositId, Keccak sessionId);
        Task SendDepositApprovalConfirmedAsync(Keccak dataAssetId, string dataAssetName);
        Task SendDepositApprovalRejectedAsync(Keccak dataAssetId, string dataAssetName);
        Task SendClaimedEarlyRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash);
        Task SendClaimedRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash);
        Task SendBlockProcessedAsync(long blockNumber);
    }
}