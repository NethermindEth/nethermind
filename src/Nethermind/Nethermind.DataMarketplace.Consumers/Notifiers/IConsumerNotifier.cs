// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Consumers.Notifiers
{
    public interface IConsumerNotifier
    {
        Task SendDataRequestResultAsync(Keccak depositId, DataRequestResult result);

        Task SendDepositConfirmationsStatusAsync(Keccak depositId, string dataAssetName, uint confirmations,
            uint requiredConfirmations, uint confirmationTimestamp, bool confirmed);

        Task SendDepositRejectedAsync(Keccak depositId);
        Task SendDataInvalidAsync(Keccak depositId, InvalidDataReason reason);
        Task SendSessionStartedAsync(Keccak depositId, Keccak sessionId);
        Task SendSessionFinishedAsync(Keccak depositId, Keccak sessionId);
        Task SendConsumerAccountLockedAsync(Address address);
        Task SendConsumerAccountUnlockedAsync(Address address);
        Task SendConsumerAddressChangedAsync(Address newAddress, Address previousAddress);
        Task SendProviderAddressChangedAsync(Address newAddress, Address previousAddress);
        Task SendDataAssetStateChangedAsync(Keccak id, string name, DataAssetState state);
        Task SendDataAssetRemovedAsync(Keccak id, string name);
        Task SendDataAvailabilityChangedAsync(Keccak depositId, Keccak sessionId, DataAvailability availability);
        Task SendDataStreamEnabledAsync(Keccak depositId, Keccak sessionId);
        Task SendDataStreamDisabledAsync(Keccak depositId, Keccak sessionId);
        Task SendDepositApprovalConfirmedAsync(Keccak dataAssetId, string dataAssetName, Address address);
        Task SendDepositApprovalRejectedAsync(Keccak dataAssetId, string dataAssetName, Address address);
        Task SendClaimedEarlyRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash);
        Task SendClaimedRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash);
        Task SendBlockProcessedAsync(long blockNumber);
        Task SendGraceUnitsExceeded(Keccak depositId, string dataAssetName, uint consumedUnitsFromProvider,
            uint consumedUnits, uint graceUnits);
        Task SendGasPriceAsync(GasPriceTypes types);
        Task SendUsdPriceAsync(string currency, decimal price, ulong updatedAt);
    }
}
