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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.DataRequests;
using Nethermind.DataMarketplace.Consumers.DataStreams;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Receipts;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    // This service acts as a bridge between the available actions for NDM consumer and JSON RPC + Subprotocol calls
    public class ConsumerService : IConsumerService
    {
        private readonly IAccountService _accountService;
        private readonly IDataAssetService _dataAssetService;
        private readonly IDataRequestService _dataRequestService;
        private readonly IDataConsumerService _dataConsumerService;
        private readonly IDataStreamService _dataStreamService;
        private readonly IDepositManager _depositManager;
        private readonly IDepositApprovalService _depositApprovalService;
        private readonly IProviderService _providerService;
        private readonly IReceiptService _receiptService;
        private readonly IRefundService _refundService;
        private readonly ISessionService _sessionService;
        private readonly IProxyService _proxyService;

        public ConsumerService(IAccountService accountService, IDataAssetService dataAssetService,
            IDataRequestService dataRequestService, IDataConsumerService dataConsumerService,
            IDataStreamService dataStreamService, IDepositManager depositManager,
            IDepositApprovalService depositApprovalService, IProviderService providerService,
            IReceiptService receiptService, IRefundService refundService, ISessionService sessionService,
            IProxyService proxyService)
        {
            _accountService = accountService;
            _dataAssetService = dataAssetService;
            _dataRequestService = dataRequestService;
            _dataConsumerService = dataConsumerService;
            _dataStreamService = dataStreamService;
            _depositManager = depositManager;
            _depositApprovalService = depositApprovalService;
            _providerService = providerService;
            _receiptService = receiptService;
            _refundService = refundService;
            _sessionService = sessionService;
            _proxyService = proxyService;
        }

        #region Accounts

        public Address GetAddress()
            => _accountService.GetAddress();

        public Task ChangeAddressAsync(Address address)
            => _accountService.ChangeAddressAsync(address);

        #endregion

        #region DataAssets

        public IReadOnlyList<DataAsset> GetDiscoveredDataAssets()
            => _dataAssetService.GetAllDiscovered();

        public Task<IReadOnlyList<DataAssetInfo>> GetKnownDataAssetsAsync()
            => _dataAssetService.GetAllKnownAsync();

        public void AddDiscoveredDataAsset(DataAsset dataAsset, INdmPeer peer)
            => _dataAssetService.AddDiscovered(dataAsset, peer);

        public void AddDiscoveredDataAssets(DataAsset[] dataAssets, INdmPeer peer)
            => _dataAssetService.AddDiscovered(dataAssets, peer);

        public void ChangeDataAssetState(Keccak dataAssetId, DataAssetState state)
            => _dataAssetService.ChangeState(dataAssetId, state);

        public void RemoveDiscoveredDataAsset(Keccak dataAssetId)
            => _dataAssetService.RemoveDiscovered(dataAssetId);
        
        #endregion
                
        #region DataRequests
        
        public Task<DataRequestResult> SendDataRequestAsync(Keccak depositId)
            => _dataRequestService.SendAsync(depositId);
        
        #endregion
        
        #region DataStreams
        
        public Task<Keccak?> EnableDataStreamAsync(Keccak depositId, string client, string?[] args)
            => _dataStreamService.EnableDataStreamAsync(depositId, client, args);

        public Task<Keccak?> DisableDataStreamAsync(Keccak depositId, string client)
            => _dataStreamService.DisableDataStreamAsync(depositId, client);

        public Task<Keccak?> DisableDataStreamsAsync(Keccak depositId)
            => _dataStreamService.DisableDataStreamsAsync(depositId);

        public Task SetEnabledDataStreamAsync(Keccak depositId, string client, string?[] args)
            => _dataStreamService.SetEnabledDataStreamAsync(depositId, client, args);

        public Task SetDisabledDataStreamAsync(Keccak depositId, string client)
            => _dataStreamService.SetDisabledDataStreamAsync(depositId, client);
        
        public Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider)
            => _dataConsumerService.SetUnitsAsync(depositId, consumedUnitsFromProvider);

        public Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability)
            => _dataConsumerService.SetDataAvailabilityAsync(depositId, dataAvailability);

        public Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason)
            => _dataConsumerService.HandleInvalidDataAsync(depositId, reason);

        public Task HandleGraceUnitsExceededAsync(Keccak depositId, uint consumedUnitsFromProvider, uint graceUnits)
            => _dataConsumerService.HandleGraceUnitsExceededAsync(depositId, consumedUnitsFromProvider, graceUnits);
        
        #endregion
        
        #region Deposits
        
        public Task<DepositDetails?> GetDepositAsync(Keccak depositId)
            => _depositManager.GetAsync(depositId);

        public Task<PagedResult<DepositDetails>> GetDepositsAsync(GetDeposits query)
            => _depositManager.BrowseAsync(query);

        public Task<Keccak?> MakeDepositAsync(Keccak assetId, uint units, UInt256 value, UInt256? gasPrice = null)
            => _depositManager.MakeAsync(assetId, units, value, _accountService.GetAddress(), gasPrice);

        public Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetConsumerDepositApprovals query)
            => _depositApprovalService.BrowseAsync(query);

        public Task<Keccak?> RequestDepositApprovalAsync(Keccak assetId, string kyc)
            => _depositApprovalService.RequestAsync(assetId, _accountService.GetAddress(), kyc);
        
        public Task ConfirmDepositApprovalAsync(Keccak assetId, Address consumer)
            => _depositApprovalService.ConfirmAsync(assetId, consumer);

        public Task RejectDepositApprovalAsync(Keccak assetId, Address consumer)
            => _depositApprovalService.RejectAsync(assetId, consumer);
        
        public Task UpdateDepositApprovalsAsync(IReadOnlyList<DepositApproval> depositApprovals, Address provider)
            => _depositApprovalService.UpdateAsync(depositApprovals, provider);
        
        #endregion
        
        #region Providers

        public IReadOnlyList<Address> GetConnectedProviders()
            => _providerService.GetPeers().Select(p => p.ProviderAddress ?? throw new InvalidDataException("Connected provider peer has no provider address set")).ToArray();
            
        public Task<IReadOnlyList<ProviderInfo>> GetKnownProvidersAsync()
            => _providerService.GetKnownAsync();
        
        public Task ChangeProviderAddressAsync(INdmPeer peer, Address address)
            => _providerService.ChangeAddressAsync(peer, address);

        public void AddProviderPeer(INdmPeer peer)
            => _providerService.Add(peer);
        
        #endregion
        
        #region Receipts
        
        public Task SendDataDeliveryReceiptAsync(DataDeliveryReceiptRequest request)
            => _receiptService.SendAsync(request);
        
        #endregion
        
        #region Refunds
        
        public Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason)
            => _refundService.SetEarlyRefundTicketAsync(ticket, reason);
        
        #endregion
        
        #region Sessions

        public IReadOnlyList<ConsumerSession> GetActiveSessions()
            => _sessionService.GetAllActive();

        public Task StartSessionAsync(Session session, INdmPeer provider)
            => _sessionService.StartSessionAsync(session, provider);

        public Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true)
            => _sessionService.FinishSessionAsync(session, provider, removePeer);

        public Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true)
            => _sessionService.FinishSessionsAsync(provider, removePeer);
        
        public Task<Keccak?> SendFinishSessionAsync(Keccak depositId)
            => _sessionService.SendFinishSessionAsync(depositId);
        
        #endregion
        
        #region Proxy

        public Task<NdmProxy?> GetProxyAsync()
            => _proxyService.GetAsync();

        public Task SetProxyAsync(IEnumerable<string> urls)
            => _proxyService.SetAsync(urls);

        #endregion
    }
}