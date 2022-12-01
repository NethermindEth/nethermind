// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
