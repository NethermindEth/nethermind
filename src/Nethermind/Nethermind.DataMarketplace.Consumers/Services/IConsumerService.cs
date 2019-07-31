/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public interface IConsumerService
    {
        event EventHandler<AddressChangedEventArgs> AddressChanged;
        Address GetAddress();
        Task ChangeAddressAsync(Address address);
        Task ChangeProviderAddressAsync(INdmPeer peer, Address address);
        void AddProviderPeer(INdmPeer peer);
        void AddDiscoveredDataHeader(DataHeader dataHeaders, INdmPeer peer);
        void AddDiscoveredDataHeaders(DataHeader[] dataHeaders, INdmPeer peer);
        void ChangeDataHeaderState(Keccak dataHeaderId, DataHeaderState state);
        void RemoveDiscoveredDataHeader(Keccak dataHeaderId);
        Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider);
        Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability);
        Task<Keccak> MakeDepositAsync(Keccak headerId, uint units, UInt256 value);
        IReadOnlyList<Address> GetConnectedProviders();
        IReadOnlyList<ConsumerSession> GetActiveSessions();
        IReadOnlyList<DataHeader> GetDiscoveredDataHeaders();
        Task<IReadOnlyList<ProviderInfo>> GetKnownProvidersAsync();
        Task<IReadOnlyList<DataHeaderInfo>> GetKnownDataHeadersAsync();
        Task<PagedResult<DepositDetails>> GetDepositsAsync(GetDeposits query);
        Task<DepositDetails> GetDepositAsync(Keccak depositId);
        Task<Keccak> SendDataRequestAsync(Keccak depositId);
        Task StartSessionAsync(Session session, INdmPeer provider);
        Task<Keccak> SendFinishSessionAsync(Keccak depositId);
        Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true);
        Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true);
        Task<Keccak> EnableDataStreamAsync(Keccak depositId, string[] args);
        Task<Keccak> DisableDataStreamAsync(Keccak depositId);
        Task SetEnabledDataStreamAsync(Keccak depositId, string[] args);
        Task SetDisabledDataStreamAsync(Keccak depositId);
        Task SendDataDeliveryReceiptAsync(DataDeliveryReceiptRequest request);
        Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason);
        Task<Keccak> RequestDepositApprovalAsync(Keccak headerId, string kyc);
        Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetConsumerDepositApprovals query);
        Task ConfirmDepositApprovalAsync(Keccak headerId);
        Task RejectDepositApprovalAsync(Keccak headerId);
        Task UpdateDepositApprovalsAsync(IReadOnlyList<DepositApproval> depositApprovals, Address provider);
        Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason);
    }
}