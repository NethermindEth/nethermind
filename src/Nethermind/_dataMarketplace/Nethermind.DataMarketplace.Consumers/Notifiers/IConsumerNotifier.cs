//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
