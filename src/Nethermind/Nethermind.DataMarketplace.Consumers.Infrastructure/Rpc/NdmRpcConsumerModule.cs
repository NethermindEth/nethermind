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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Int256;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc
{
    public class NdmRpcConsumerModule : INdmRpcConsumerModule
    {
        private readonly IConsumerService _consumerService;
        private readonly IDepositReportService _depositReportService;
        private readonly IJsonRpcNdmConsumerChannel _jsonRpcNdmConsumerChannel;
        private readonly IEthRequestService _ethRequestService;
        private readonly IGasPriceService _gasPriceService;
        private readonly IConsumerTransactionsService _transactionsService;
        private readonly IConsumerGasLimitsService _gasLimitsService;
        private readonly IWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly IPriceService _priceService;

        public NdmRpcConsumerModule(
            IConsumerService consumerService,
            IDepositReportService depositReportService,
            IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel,
            IEthRequestService ethRequestService,
            IGasPriceService gasPriceService,
            IConsumerTransactionsService transactionsService,
            IConsumerGasLimitsService gasLimitsService,
            IWallet personalBridge,
            ITimestamper timestamper,
            IPriceService priceService)
        {
            _consumerService = consumerService ?? throw new ArgumentNullException(nameof(consumerService));
            _depositReportService = depositReportService ?? throw new ArgumentNullException(nameof(depositReportService));
            _jsonRpcNdmConsumerChannel = jsonRpcNdmConsumerChannel ?? throw new ArgumentNullException(nameof(jsonRpcNdmConsumerChannel));
            _ethRequestService = ethRequestService ?? throw new ArgumentNullException(nameof(ethRequestService));
            _gasPriceService = gasPriceService ?? throw new ArgumentNullException(nameof(gasPriceService));
            _transactionsService = transactionsService ?? throw new ArgumentNullException(nameof(transactionsService));
            _gasLimitsService = gasLimitsService ?? throw new ArgumentNullException(nameof(gasLimitsService));
            _wallet = personalBridge ?? throw new ArgumentNullException(nameof(personalBridge));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _priceService = priceService ?? throw new ArgumentNullException(nameof(priceService));
        }

        public ResultWrapper<AccountForRpc[]> ndm_listAccounts()
        {
            AccountForRpc[] accounts = _wallet.GetAccounts().Select(a => new AccountForRpc
            {
                Address = a,
                Unlocked = _wallet.IsUnlocked(a)
            }).ToArray();

            return ResultWrapper<AccountForRpc[]>.Success(accounts);
        }

        public ResultWrapper<Address> ndm_getConsumerAddress()
            => ResultWrapper<Address>.Success(_consumerService.GetAddress());

        public async Task<ResultWrapper<Address>> ndm_changeConsumerAddress(Address address)
        {
            await _consumerService.ChangeAddressAsync(address);

            return ResultWrapper<Address>.Success(address);
        }

        public ResultWrapper<DataAssetForRpc[]> ndm_getDiscoveredDataAssets()
            => ResultWrapper<DataAssetForRpc[]>.Success(_consumerService.GetDiscoveredDataAssets()
                .Select(d => new DataAssetForRpc(d)).ToArray());

        public async Task<ResultWrapper<DataAssetInfoForRpc[]>> ndm_getKnownDataAssets()
        {
            IReadOnlyList<DataAssetInfo> dataAssets = await _consumerService.GetKnownDataAssetsAsync();

            return ResultWrapper<DataAssetInfoForRpc[]>.Success(dataAssets
                .Select(d => new DataAssetInfoForRpc(d)).ToArray());
        }

        public async Task<ResultWrapper<ProviderInfoForRpc[]>> ndm_getKnownProviders()
        {
            IReadOnlyList<ProviderInfo> providers = await _consumerService.GetKnownProvidersAsync();

            return ResultWrapper<ProviderInfoForRpc[]>.Success(providers
                .Select(p => new ProviderInfoForRpc(p)).ToArray());
        }

        public ResultWrapper<Address[]> ndm_getConnectedProviders()
            => ResultWrapper<Address[]>.Success(_consumerService.GetConnectedProviders().ToArray());

        public ResultWrapper<ConsumerSessionForRpc[]> ndm_getActiveConsumerSessions()
            => ResultWrapper<ConsumerSessionForRpc[]>.Success(_consumerService.GetActiveSessions()
                .Select(s => new ConsumerSessionForRpc(s)).ToArray());

        public async Task<ResultWrapper<PagedResult<DepositDetailsForRpc>>> ndm_getDeposits(GetDeposits? query)
        {
            uint timestamp = (uint) _timestamper.UnixTime.Seconds;
            PagedResult<DepositDetails> deposits = await _consumerService.GetDepositsAsync(query ?? new GetDeposits
            {
                Results = int.MaxValue
            });

            return ResultWrapper<PagedResult<DepositDetailsForRpc>>.Success(PagedResult<DepositDetailsForRpc>.From(
                deposits, deposits.Items.Select(d => new DepositDetailsForRpc(d, timestamp)).ToArray()));
        }

        public async Task<ResultWrapper<DepositDetailsForRpc>> ndm_getDeposit(Keccak depositId)
        {
            uint timestamp = (uint) _timestamper.UnixTime.Seconds;
            DepositDetails? deposit = await _consumerService.GetDepositAsync(depositId);

            return deposit is null
                ? ResultWrapper<DepositDetailsForRpc>.Fail($"Deposit: '{depositId}' was not found.")
                : ResultWrapper<DepositDetailsForRpc>.Success(new DepositDetailsForRpc(deposit, timestamp));
        }

        public async Task<ResultWrapper<Keccak>> ndm_makeDeposit(MakeDepositForRpc deposit, UInt256? gasPrice = null)
        {
            if(deposit.DataAssetId == null)
            {
                return ResultWrapper<Keccak>.Fail("Deposit couldn't be made - asset ID unknown.");
            }
            
            Keccak? depositId = await _consumerService.MakeDepositAsync(deposit.DataAssetId, deposit.Units, deposit.Value,
                gasPrice);

            return depositId is null
                ? ResultWrapper<Keccak>.Fail("Deposit couldn't be made.")
                : ResultWrapper<Keccak>.Success(depositId);
        }

        public async Task<ResultWrapper<string>> ndm_sendDataRequest(Keccak depositId)
        {
            DataRequestResult result = await _consumerService.SendDataRequestAsync(depositId);
            return ResultWrapper<string>.Success(result.ToString());
        }

        public async Task<ResultWrapper<Keccak>> ndm_finishSession(Keccak depositId)
            => await _consumerService.SendFinishSessionAsync(depositId) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't finish session for deposit: '{depositId}'.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_enableDataStream(Keccak depositId, string client, string[] args)
            => await _consumerService.EnableDataStreamAsync(depositId, client, args) is null
                ? ResultWrapper<Keccak>.Fail(
                    $"Couldn't enable data stream for deposit: '{depositId}', client: {client}.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_disableDataStream(Keccak depositId, string client)
            => await _consumerService.DisableDataStreamAsync(depositId, client) is null
                ? ResultWrapper<Keccak>.Fail(
                    $"Couldn't disable data stream for deposit: '{depositId}', client: {client}.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_disableDataStreams(Keccak depositId)
            => await _consumerService.DisableDataStreamsAsync(depositId) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't disable data streams for deposit: '{depositId}'.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<DepositsReportForRpc>> ndm_getDepositsReport(GetDepositsReport? query = null)
        {
            DepositsReport report = await _depositReportService.GetAsync(query ?? new GetDepositsReport());

            return ResultWrapper<DepositsReportForRpc>.Success(new DepositsReportForRpc(report));
        }

        public async Task<ResultWrapper<PagedResult<DepositApprovalForRpc>>> ndm_getConsumerDepositApprovals(
            GetConsumerDepositApprovals? query = null)
        {
            PagedResult<DepositApproval> depositApprovals = await _consumerService.GetDepositApprovalsAsync(
                query ?? new GetConsumerDepositApprovals
                {
                    Results = int.MaxValue
                });

            return ResultWrapper<PagedResult<DepositApprovalForRpc>>.Success(PagedResult<DepositApprovalForRpc>.From(
                depositApprovals, depositApprovals.Items.Select(d => new DepositApprovalForRpc(d)).ToArray()));
        }

        public async Task<ResultWrapper<Keccak>> ndm_requestDepositApproval(Keccak assetId, string kyc)
        {
            Keccak? id = await _consumerService.RequestDepositApprovalAsync(assetId, kyc);

            return id is null
                ? ResultWrapper<Keccak>.Fail($"Deposit approval for data asset: '{assetId} couldn't be requested.")
                : ResultWrapper<Keccak>.Success(id);
        }

        public async Task<ResultWrapper<FaucetResponseForRpc>> ndm_requestEth(Address address)
        {
            FaucetResponse response = await _ethRequestService.TryRequestEthAsync(address, 1.Ether());

            return ResultWrapper<FaucetResponseForRpc>.Success(new FaucetResponseForRpc(response));
        }

        public ResultWrapper<string?> ndm_pullData(Keccak depositId)
        {
            string? data = _jsonRpcNdmConsumerChannel.Pull(depositId);
            return ResultWrapper<string?>.Success(data);
        }

        public async Task<ResultWrapper<NdmProxyResponseForRpc>> ndm_getProxy()
        {
            NdmProxy? proxy = await _consumerService.GetProxyAsync();
            if (proxy == null)
            {
                return ResultWrapper<NdmProxyResponseForRpc>.Success(new NdmProxyResponseForRpc
                {
                    Enabled = false,
                    Urls = Array.Empty<string>()
                });
            }

            return ResultWrapper<NdmProxyResponseForRpc>.Success(new NdmProxyResponseForRpc
            {
                Enabled = proxy.Enabled,
                Urls = proxy.Urls
            });
        }

        public async Task<ResultWrapper<bool>> ndm_setProxy(string[] urls)
        {
            await _consumerService.SetProxyAsync(urls);

            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<UsdPriceForRpc> ndm_getUsdPrice(string currency)
        {
            var priceInfo = _priceService.Get(currency);
            return priceInfo is null
                ? ResultWrapper<UsdPriceForRpc>.Fail($"{currency} couldn't be requested.")
                : ResultWrapper<UsdPriceForRpc>.Success(new UsdPriceForRpc(priceInfo.UsdPrice, priceInfo.UpdatedAt));
        }

        public ResultWrapper<GasPriceTypesForRpc> ndm_getGasPrice()
            => _gasPriceService.Types is null
                ? ResultWrapper<GasPriceTypesForRpc>.Fail("Gas price couldn't be requested.")
                : ResultWrapper<GasPriceTypesForRpc>.Success(new GasPriceTypesForRpc(_gasPriceService.Types));

        public async Task<ResultWrapper<bool>> ndm_setGasPrice(string gasPriceOrType)
        {
            await _gasPriceService.SetGasPriceOrTypeAsync(gasPriceOrType);

            return ResultWrapper<bool>.Success(true);
        }

        public async Task<ResultWrapper<bool>> ndm_setRefundGasPrice(UInt256 gasPrice)
        {
            await _gasPriceService.SetRefundGasPriceAsync(gasPrice);

            return ResultWrapper<bool>.Success(true);
        }

        public async Task<ResultWrapper<UInt256>> ndm_getRefundGasPrice()
            => ResultWrapper<UInt256>.Success(await _gasPriceService.GetCurrentRefundGasPriceAsync());
        
        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updateDepositGasPrice(Keccak depositId,
            UInt256 gasPrice)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(new UpdatedTransactionInfoForRpc(
                await _transactionsService.UpdateDepositGasPriceAsync(depositId, gasPrice)));

        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updateRefundGasPrice(Keccak depositId,
            UInt256 gasPrice)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(new UpdatedTransactionInfoForRpc(
                await _transactionsService.UpdateRefundGasPriceAsync(depositId, gasPrice)));
        
        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelDeposit(Keccak depositId)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(
                new UpdatedTransactionInfoForRpc(await _transactionsService.CancelDepositAsync(depositId)));

        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelRefund(Keccak depositId)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(
                new UpdatedTransactionInfoForRpc(await _transactionsService.CancelRefundAsync(depositId)));

        public async Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getConsumerPendingTransactions()
        {
            IEnumerable<ResourceTransaction> transactions = await _transactionsService.GetPendingAsync();

            return ResultWrapper<IEnumerable<ResourceTransactionForRpc>>.Success(transactions
                .Select(t => new ResourceTransactionForRpc(t)));
        }
        
        public async Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getAllConsumerTransactions()
        {
            IEnumerable<ResourceTransaction> transactions = await _transactionsService.GetAllTransactionsAsync();

            return ResultWrapper<IEnumerable<ResourceTransactionForRpc>>.Success(transactions
                .Select(t => new ResourceTransactionForRpc(t)));
        }

        public ResultWrapper<GasLimitsForRpc> ndm_getConsumerGasLimits()
            => ResultWrapper<GasLimitsForRpc>.Success(new GasLimitsForRpc(_gasLimitsService.GasLimits));
    }
}
