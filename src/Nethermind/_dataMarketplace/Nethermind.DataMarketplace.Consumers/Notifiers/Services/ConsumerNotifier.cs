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
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Consumers.Notifiers.Services
{
    public class ConsumerNotifier : IConsumerNotifier
    {
        private readonly INdmNotifier _notifier;

        public ConsumerNotifier(INdmNotifier notifier)
        {
            _notifier = notifier;
        }

        public Task SendDataRequestResultAsync(Keccak depositId, DataRequestResult result)
            => _notifier.NotifyAsync(new Notification("data_request_result",
                new
                {
                    depositId,
                    result = result.ToString()
                }));

        public Task SendDepositConfirmationsStatusAsync(Keccak depositId, string dataAssetName, uint confirmations,
            uint requiredConfirmations, uint confirmationTimestamp, bool confirmed)
            => _notifier.NotifyAsync(new Notification("deposit_confirmations",
                new
                {
                    depositId,
                    dataAssetName,
                    confirmations,
                    requiredConfirmations,
                    confirmationTimestamp,
                    confirmed
                }));

        public Task SendDepositRejectedAsync(Keccak depositId)
            => _notifier.NotifyAsync(new Notification("deposit_rejected",
                new
                {
                    depositId
                }));

        public Task SendDataInvalidAsync(Keccak depositId, InvalidDataReason reason)
            => _notifier.NotifyAsync(new Notification("data_invalid",
                new
                {
                    depositId,
                    reason = reason.ToString()
                }));

        public Task SendSessionStartedAsync(Keccak depositId, Keccak sessionId)
            => _notifier.NotifyAsync(new Notification("session_started",
                new
                {
                    depositId,
                    sessionId
                }));

        public Task SendSessionFinishedAsync(Keccak depositId, Keccak sessionId)
            => _notifier.NotifyAsync(new Notification("session_finished",
                new
                {
                    depositId,
                    sessionId,
                }));

        public Task SendConsumerAccountLockedAsync(Address address)
            => _notifier.NotifyAsync(new Notification("consumer_account_locked",
                new
                {
                    address
                }));

        public Task SendConsumerAccountUnlockedAsync(Address address)
            => _notifier.NotifyAsync(new Notification("consumer_account_unlocked",
                new
                {
                    address
                }));
        public Task SendConsumerAddressChangedAsync(Address newAddress, Address previousAddress)
            => _notifier.NotifyAsync(new Notification("consumer_address_changed",
                new
                {
                    newAddress,
                    previousAddress
                }));

        public Task SendProviderAddressChangedAsync(Address newAddress, Address previousAddress)
            => _notifier.NotifyAsync(new Notification("provider_address_changed",
                new
                {
                    newAddress,
                    previousAddress
                }));

        public Task SendDataAssetStateChangedAsync(Keccak id, string name, DataAssetState state)
            => _notifier.NotifyAsync(new Notification("data_asset_state_changed",
                new
                {
                    id,
                    name,
                    state = state.ToString()
                }));

        public Task SendDataAssetRemovedAsync(Keccak id, string name)
            => _notifier.NotifyAsync(new Notification("data_asset_removed",
                new
                {
                    id,
                    name
                }));

        public Task SendDataAvailabilityChangedAsync(Keccak depositId, Keccak sessionId, DataAvailability availability)
            => _notifier.NotifyAsync(new Notification("data_availability_changed",
                new
                {
                    depositId,
                    sessionId,
                    availability = availability.ToString()
                }));

        public Task SendDataStreamEnabledAsync(Keccak depositId, Keccak sessionId)
            => _notifier.NotifyAsync(new Notification("data_stream_enabled",
                new
                {
                    depositId,
                    sessionId
                }));

        public Task SendDataStreamDisabledAsync(Keccak depositId, Keccak sessionId)
            => _notifier.NotifyAsync(new Notification("data_stream_disabled",
                new
                {
                    depositId,
                    sessionId
                }));

        public Task SendDepositApprovalConfirmedAsync(Keccak dataAssetId, string dataAssetName, Address address)
            => _notifier.NotifyAsync(new Notification("deposit_approval_confirmed",
                new
                {
                    dataAssetId,
                    dataAssetName,
                    address
                }));

        public Task SendDepositApprovalRejectedAsync(Keccak dataAssetId, string dataAssetName, Address address)
            => _notifier.NotifyAsync(new Notification("deposit_approval_rejected",
                new
                {
                    dataAssetId,
                    dataAssetName,
                    address
                }));

        public Task SendClaimedEarlyRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash)
            => _notifier.NotifyAsync(new Notification("claimed_early_refund",
                new
                {
                    depositId,
                    dataAssetName,
                    transactionHash
                }));

        public Task SendClaimedRefundAsync(Keccak depositId, string dataAssetName, Keccak transactionHash)
            => _notifier.NotifyAsync(new Notification("claimed_refund",
                new
                {
                    depositId,
                    dataAssetName,
                    transactionHash
                }));

        public Task SendBlockProcessedAsync(long blockNumber)
            => _notifier.NotifyAsync(new Notification("block_processed",
                new
                {
                    blockNumber
                }));

        public Task SendGraceUnitsExceeded(Keccak depositId, string dataAssetName, uint consumedUnitsFromProvider,
            uint consumedUnits, uint graceUnits)
            => _notifier.NotifyAsync(new Notification("grace_units_exceeded",
                new
                {
                    depositId,
                    dataAssetName,
                    consumedUnitsFromProvider,
                    consumedUnits,
                    graceUnits
                }));

        public Task SendGasPriceAsync(GasPriceTypes types)
            => _notifier.NotifyAsync(new Notification("gas_price",
                new
                {
                    safeLow = new
                    {
                        price = types.SafeLow.Price,
                        waitTime = types.SafeLow.WaitTime
                    },
                    average = new
                    {
                        price = types.Average.Price,
                        waitTime = types.Average.WaitTime
                    },
                    fast = new
                    {
                        price = types.Fast.Price,
                        waitTime = types.Fast.WaitTime
                    },
                    fastest = new
                    {
                        price = types.Fastest.Price,
                        waitTime = types.Fastest.WaitTime
                    },
                    custom = new
                    {
                        price = types.Custom.Price,
                        waitTime = types.Custom.WaitTime
                    },
                    type = types.Type,
                    updatedAt = types.UpdatedAt
                }));

        public Task SendUsdPriceAsync(string currency, decimal price, ulong updatedAt)
            => _notifier.NotifyAsync(new Notification("usd_price",
                new
                {
                    currency,
                    price,
                    updatedAt
                }));
    }
}
