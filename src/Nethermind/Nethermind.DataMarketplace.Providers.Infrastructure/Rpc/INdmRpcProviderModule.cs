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

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc
{
    [RpcModule(ModuleType.NdmProvider)]
    public interface INdmRpcProviderModule : IRpcModule
    {
        ResultWrapper<string[]> ndm_getProviderPlugins();
        ResultWrapper<Address> ndm_getProviderAddress();
        ResultWrapper<Address> ndm_getProviderColdWalletAddress();
        Task<ResultWrapper<Address>> ndm_changeProviderAddress(Address address);
        Task<ResultWrapper<Address>> ndm_changeProviderColdWalletAddress(Address address);
        Task<ResultWrapper<ConsumerDetailsForRpc>> ndm_getConsumer(Keccak depositId);
        Task<ResultWrapper<PagedResult<ConsumerForRpc>>> ndm_getConsumers(GetConsumers? query = null);
        Task<ResultWrapper<PagedResult<DataAssetForRpc>>> ndm_getDataAssets(GetDataAssets? query = null);
        Task<ResultWrapper<Keccak>> ndm_addDataAsset(DataAssetForRpc dataAsset);
        Task<ResultWrapper<bool>> ndm_removeDataAsset(Keccak assetId);
        Task<ResultWrapper<bool>> ndm_changeDataAssetState(Keccak assetId, string state);
        Task<ResultWrapper<bool>> ndm_changeDataAssetPlugin(Keccak assetId, string? plugin = null);
        Task<ResultWrapper<Keccak>> ndm_sendData(DataAssetDataForRpc data);
        Task<ResultWrapper<Keccak>> ndm_sendEarlyRefundTicket(Keccak depositId, string? reason = null);
        Task<ResultWrapper<ConsumersReportForRpc>> ndm_getConsumersReport(GetConsumersReport? query = null);
        Task<ResultWrapper<PaymentClaimsReportForRpc>> ndm_getPaymentClaimsReport(GetPaymentClaimsReport? query = null);
        Task<ResultWrapper<PagedResult<DepositApprovalForRpc>>> ndm_getProviderDepositApprovals(
            GetProviderDepositApprovals? query = null);
        Task<ResultWrapper<Keccak>> ndm_confirmDepositApproval(Keccak assetId, Address consumer);
        Task<ResultWrapper<Keccak>> ndm_rejectDepositApproval(Keccak assetId, Address consumer);
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updatePaymentClaimGasPrice(Keccak paymentClaimId, UInt256 gasPrice);
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelPaymentClaim(Keccak paymentClaimId);
        Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getProviderPendingTransactions();
        Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getAllProviderTransactions();
        ResultWrapper<GasLimitsForRpc> ndm_getProviderGasLimits();
        Task<ResultWrapper<bool>> ndm_setPaymentClaimGasPrice(UInt256 gasPrice);
        Task<ResultWrapper<bool>> ndm_setReceiptRequestThreshold(UInt256 gasPrice);
        Task<ResultWrapper<bool>> ndm_setReceiptsMergeThreshold(UInt256 gasPrice);
        Task<ResultWrapper<bool>> ndm_setPaymentClaimThreshold(UInt256 gasPrice);
        Task<ResultWrapper<UInt256>> ndm_getPaymentClaimGasPrice();
        Task<ResultWrapper<UInt256>> ndm_getReceiptRequestThreshold();
        Task<ResultWrapper<UInt256>> ndm_getReceiptsMergeThreshold();
        Task<ResultWrapper<UInt256>> ndm_getPaymentClaimThreshold();
    }
}
