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
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Personal;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc
{
    [RpcModule(ModuleType.NdmConsumer)]
    public interface INdmRpcConsumerModule : IRpcModule
    {
        ResultWrapper<AccountForRpc[]> ndm_listAccounts();
        ResultWrapper<Address> ndm_getConsumerAddress();
        Task<ResultWrapper<Address>> ndm_changeConsumerAddress(Address address);
        ResultWrapper<DataAssetForRpc[]> ndm_getDiscoveredDataAssets();
        Task<ResultWrapper<DataAssetInfoForRpc[]>> ndm_getKnownDataAssets();
        Task<ResultWrapper<ProviderInfoForRpc[]>> ndm_getKnownProviders();
        ResultWrapper<Address[]> ndm_getConnectedProviders();
        ResultWrapper<ConsumerSessionForRpc[]> ndm_getActiveConsumerSessions();
        Task<ResultWrapper<PagedResult<DepositDetailsForRpc>>> ndm_getDeposits(GetDeposits? query = null);
        Task<ResultWrapper<DepositDetailsForRpc>> ndm_getDeposit(Keccak depositId);
        Task<ResultWrapper<Keccak>> ndm_makeDeposit(MakeDepositForRpc deposit, UInt256? gasPrice = null);
        Task<ResultWrapper<string>> ndm_sendDataRequest(Keccak depositId);
        Task<ResultWrapper<Keccak>> ndm_finishSession(Keccak depositId);
        Task<ResultWrapper<Keccak>> ndm_enableDataStream(Keccak depositId, string client, string[] args);
        Task<ResultWrapper<Keccak>> ndm_disableDataStream(Keccak depositId, string client);
        Task<ResultWrapper<Keccak>> ndm_disableDataStreams(Keccak depositId);
        ResultWrapper<string?> ndm_pullData(Keccak depositId);
        Task<ResultWrapper<DepositsReportForRpc>> ndm_getDepositsReport(GetDepositsReport? query = null);

        Task<ResultWrapper<PagedResult<DepositApprovalForRpc>>> ndm_getConsumerDepositApprovals(
            GetConsumerDepositApprovals? query = null);

        Task<ResultWrapper<Keccak>> ndm_requestDepositApproval(Keccak assetId, string kyc);
        Task<ResultWrapper<FaucetResponseForRpc>> ndm_requestEth(Address address);
        Task<ResultWrapper<NdmProxyResponseForRpc>> ndm_getProxy();
        Task<ResultWrapper<bool>> ndm_setProxy(string[] urls);
        ResultWrapper<UsdPriceForRpc> ndm_getUsdPrice(string currency);
        ResultWrapper<GasPriceTypesForRpc> ndm_getGasPrice();
        Task<ResultWrapper<bool>> ndm_setGasPrice(string gasPriceOrType);
        Task<ResultWrapper<bool>> ndm_setRefundGasPrice(UInt256 gasPrice);
        Task<ResultWrapper<UInt256>> ndm_getRefundGasPrice();
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updateDepositGasPrice(Keccak depositId, UInt256 gasPrice);
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updateRefundGasPrice(Keccak depositId, UInt256 gasPrice);
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelDeposit(Keccak depositId);
        Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelRefund(Keccak depositId);
        Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getConsumerPendingTransactions();
        Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getAllConsumerTransactions();
        ResultWrapper<GasLimitsForRpc> ndm_getConsumerGasLimits();
    }
}
