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

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Shared
{
    public interface IConsumerService
    {
        #region Accounts
        
        Address GetAddress();
        Task ChangeAddressAsync(Address address);
        
        #endregion
        
        #region DataAssets
        IReadOnlyList<DataAsset> GetDiscoveredDataAssets();
        Task<IReadOnlyList<DataAssetInfo>> GetKnownDataAssetsAsync();
        void AddDiscoveredDataAsset(DataAsset dataAssets, INdmPeer peer);
        void AddDiscoveredDataAssets(DataAsset[] dataAssets, INdmPeer peer);
        void ChangeDataAssetState(Keccak dataAssetId, DataAssetState state);
        void RemoveDiscoveredDataAsset(Keccak dataAssetId);
        #endregion
        
        #region DataRequests
        Task<DataRequestResult> SendDataRequestAsync(Keccak depositId);
        #endregion
        
        #region DataStreams
        
        Task<Keccak?> EnableDataStreamAsync(Keccak depositId, string client, string?[] args);
        Task<Keccak?> DisableDataStreamAsync(Keccak depositId, string client);
        Task<Keccak?> DisableDataStreamsAsync(Keccak depositId);
        Task SetEnabledDataStreamAsync(Keccak depositId, string client, string?[] args);
        Task SetDisabledDataStreamAsync(Keccak depositId, string client);
        
        Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider);
        Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability);
        Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason);
        Task HandleGraceUnitsExceededAsync(Keccak depositId, uint consumedUnitsFromProvider, uint graceUnits);
        
        #endregion
        
        #region Deposits
        
        Task<DepositDetails?> GetDepositAsync(Keccak depositId);
        Task<PagedResult<DepositDetails>> GetDepositsAsync(GetDeposits query);
        Task<Keccak?> MakeDepositAsync(Keccak assetId, uint units, UInt256 value, UInt256? gasPrice = null);

        Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetConsumerDepositApprovals query);
        Task<Keccak?> RequestDepositApprovalAsync(Keccak assetId, string kyc);
        Task ConfirmDepositApprovalAsync(Keccak assetId, Address consumer);
        Task RejectDepositApprovalAsync(Keccak assetId, Address consumer);
        Task UpdateDepositApprovalsAsync(IReadOnlyList<DepositApproval> depositApprovals, Address provider);
        
        #endregion
        
        #region Providers
        
        IReadOnlyList<Address> GetConnectedProviders();
        Task<IReadOnlyList<ProviderInfo>> GetKnownProvidersAsync();
        void AddProviderPeer(INdmPeer peer);
        Task ChangeProviderAddressAsync(INdmPeer peer, Address address);
        
        #endregion
        
        #region Receipts
        
        Task SendDataDeliveryReceiptAsync(DataDeliveryReceiptRequest request);

        #endregion
        
        #region Refunds
        
        Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason);
        
        #endregion
        
        #region Sessions
        
        IReadOnlyList<ConsumerSession> GetActiveSessions();
        Task StartSessionAsync(Session session, INdmPeer provider);
        Task<Keccak?> SendFinishSessionAsync(Keccak depositId);
        Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true);
        Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true);
        
        #endregion

        #region Proxy
        
        Task<NdmProxy?> GetProxyAsync();
        Task SetProxyAsync(IEnumerable<string> urls);
        
        #endregion
    }
}