// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
