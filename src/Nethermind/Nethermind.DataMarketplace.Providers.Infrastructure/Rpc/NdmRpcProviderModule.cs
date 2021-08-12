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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc
{
    internal class NdmRpcProviderModule : INdmRpcProviderModule
    {
        private readonly IProviderService _providerService;
        private readonly IReportService _reportService;
        private readonly IProviderTransactionsService _providerTransactionsService;
        private readonly IProviderGasLimitsService _gasLimitsService;
        private readonly IGasPriceService _gasPriceService;
        private readonly IProviderThresholdsService _providerThresholdsService;
        private readonly IDepositManager _depositManager;

        public NdmRpcProviderModule(IProviderService providerService, IReportService reportService,
            IProviderTransactionsService providerTransactionsService, IProviderGasLimitsService gasLimitsService,
            IGasPriceService gasPriceService, IProviderThresholdsService providerThresholdsService,
            IDepositManager depositManager)
        {
            _providerService = providerService;
            _reportService = reportService;
            _providerTransactionsService = providerTransactionsService;
            _gasLimitsService = gasLimitsService;
            _gasPriceService = gasPriceService;
            _providerThresholdsService = providerThresholdsService;
            _depositManager = depositManager;
        }

        public ResultWrapper<string[]> ndm_getProviderPlugins()
            => ResultWrapper<string[]>.Success(_providerService.GetPlugins());

        public ResultWrapper<Address> ndm_getProviderAddress()
            => ResultWrapper<Address>.Success(_providerService.GetAddress());

        public ResultWrapper<Address> ndm_getProviderColdWalletAddress()
            => ResultWrapper<Address>.Success(_providerService.GetColdWalletAddress());

        public async Task<ResultWrapper<Address>> ndm_changeProviderAddress(Address address)
        {
            await _providerService.ChangeAddressAsync(address);

            return ResultWrapper<Address>.Success(address);
        }

        public async Task<ResultWrapper<Address>> ndm_changeProviderColdWalletAddress(Address address)
        {
            await _providerService.ChangeColdWalletAddressAsync(address);

            return ResultWrapper<Address>.Success(address);
        }

        public async Task<ResultWrapper<bool>> ndm_changeDataAssetState(Keccak assetId, string state)
        {
            if (!Enum.TryParse<DataAssetState>(state, true, out var dataAssetState))
            {
                return ResultWrapper<bool>.Fail($"Invalid state: '{state}'.");
            }

            var changed = await _providerService.ChangeDataAssetStateAsync(assetId, dataAssetState);

            return ResultWrapper<bool>.Success(changed);
        }

        public async Task<ResultWrapper<bool>> ndm_changeDataAssetPlugin(Keccak assetId, string? plugin = null)
        {
            var changed = await _providerService.ChangeDataAssetPluginAsync(assetId, plugin);

            return ResultWrapper<bool>.Success(changed);
        }

        public async Task<ResultWrapper<Keccak>> ndm_sendData(DataAssetDataForRpc data)
        {
            if (data.AssetId == null)
            {
                throw new InvalidDataException("AssetID missing in Data Asset Data");
            }
            
            await _providerService.SendDataAssetDataAsync(new DataAssetData(data.AssetId, data.Data));
            return ResultWrapper<Keccak>.Success(data.AssetId);
        }

        public async Task<ResultWrapper<ConsumerDetailsForRpc>> ndm_getConsumer(Keccak depositId)
        {
            var consumer = await _providerService.GetConsumerAsync(depositId);
            var unclaimedUnits = _depositManager.GetUnclaimedUnits(depositId);

            return consumer is null
                ? ResultWrapper<ConsumerDetailsForRpc>.Fail($"Consumer for deposit: '{depositId}' was not found.")
                : ResultWrapper<ConsumerDetailsForRpc>.Success(new ConsumerDetailsForRpc(consumer, unclaimedUnits));
        }

        public async Task<ResultWrapper<PagedResult<ConsumerForRpc>>> ndm_getConsumers(GetConsumers? query = null)
        {
            var consumers = await _providerService.GetConsumersAsync(query ?? new GetConsumers
            {
                Results = int.MaxValue
            });

            return ResultWrapper<PagedResult<ConsumerForRpc>>.Success(PagedResult<ConsumerForRpc>.From(consumers,
                consumers.Items.Select(c => new ConsumerForRpc(c)).ToArray()));
        }

        public async Task<ResultWrapper<PagedResult<DataAssetForRpc>>> ndm_getDataAssets(GetDataAssets? query = null)
        {
            var headers = await _providerService.GetDataAssetsAsync(query ?? new GetDataAssets
            {
                Results = int.MaxValue
            });

            return ResultWrapper<PagedResult<DataAssetForRpc>>.Success(PagedResult<DataAssetForRpc>.From(headers,
                headers.Items.Select(d => new DataAssetForRpc(d)).ToArray()));
        }

        public async Task<ResultWrapper<Keccak>> ndm_addDataAsset(DataAssetForRpc dataAsset)
        {
            if (dataAsset.UnitPrice == null)
            {
                throw new InvalidDataException("Data asset is missing unit price.");
            }
            
            if (dataAsset.UnitType == null)
            {
                throw new InvalidDataException("Data asset is missing unit type.");
            }
            
            if (dataAsset.Rules == null)
            {
                throw new InvalidDataException("Data asset is missing basic rules definitions.");
            }
            
            if (dataAsset.Description == null)
            {
                throw new InvalidDataException("Data asset is missing description.");
            }
            
            if (dataAsset.Name == null)
            {
                throw new InvalidDataException("Data asset is missing name.");
            }
            
            if (dataAsset.Rules.Expiry == null)
            {
                throw new InvalidDataException("Data asset is missing expiry rule.");
            }

            Keccak? id = await _providerService.AddDataAssetAsync(dataAsset.Name,
                dataAsset.Description, (UInt256) dataAsset.UnitPrice,
                Enum.Parse<DataAssetUnitType>(dataAsset.UnitType, true),
                dataAsset.MinUnits, dataAsset.MaxUnits,
                new DataAssetRules(new DataAssetRule((UInt256) dataAsset.Rules.Expiry.Value),
                    dataAsset.Rules.UpfrontPayment is null
                        ? null
                        : new DataAssetRule((UInt256) dataAsset.Rules.UpfrontPayment.Value)),
                dataAsset.File, dataAsset.Data,
                string.IsNullOrWhiteSpace(dataAsset.QueryType)
                    ? QueryType.Stream
                    : Enum.Parse<QueryType>(dataAsset.QueryType, true),
                dataAsset.TermsAndConditions, dataAsset.KycRequired ?? false, dataAsset.Plugin);

            return id is null
                ? ResultWrapper<Keccak>.Fail($"Data asset: '{dataAsset.Name}' already exists.")
                : ResultWrapper<Keccak>.Success(id);
        }

        public async Task<ResultWrapper<bool>> ndm_removeDataAsset(Keccak assetId)
        {
            var removed = await _providerService.RemoveDataAssetAsync(assetId);

            return removed
                ? ResultWrapper<bool>.Success(true)
                : ResultWrapper<bool>.Fail($"Couldn't remove a data asset with id: '{assetId}'.");
        }

        public async Task<ResultWrapper<Keccak>> ndm_sendEarlyRefundTicket(Keccak depositId, string? reason = null)
        {
            var refundReason = RefundReason.DataDiscontinued;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                refundReason = Enum.Parse<RefundReason>(reason, true);
            }

            return await _providerService.SendEarlyRefundTicketAsync(depositId, refundReason) is null
                ? ResultWrapper<Keccak>.Fail($"Deposit: '{depositId}' was not found.")
                : ResultWrapper<Keccak>.Success(depositId);
        }

        public async Task<ResultWrapper<ConsumersReportForRpc>> ndm_getConsumersReport(GetConsumersReport? query = null)
        {
            var report = await _reportService.GetConsumersReportAsync(query ?? new GetConsumersReport());

            return ResultWrapper<ConsumersReportForRpc>.Success(new ConsumersReportForRpc(report));
        }

        public async Task<ResultWrapper<PaymentClaimsReportForRpc>> ndm_getPaymentClaimsReport(
            GetPaymentClaimsReport? query = null)
        {
            var report = await _reportService.GetPaymentClaimsReportAsync(query ?? new GetPaymentClaimsReport());

            return ResultWrapper<PaymentClaimsReportForRpc>.Success(new PaymentClaimsReportForRpc(report));
        }

        public async Task<ResultWrapper<PagedResult<DepositApprovalForRpc>>> ndm_getProviderDepositApprovals(
            GetProviderDepositApprovals? query = null)
        {
            var depositApprovals = await _providerService.GetDepositApprovalsAsync(query ?? new GetProviderDepositApprovals
            {
                Results = int.MaxValue
            });

            return ResultWrapper<PagedResult<DepositApprovalForRpc>>.Success(PagedResult<DepositApprovalForRpc>.From(
                depositApprovals, depositApprovals.Items.Select(d => new DepositApprovalForRpc(d)).ToArray()));
        }

        public async Task<ResultWrapper<Keccak>> ndm_confirmDepositApproval(Keccak assetId, Address consumer)
            => await _providerService.ConfirmDepositApprovalAsync(assetId, consumer) is null
                ? ResultWrapper<Keccak>.Fail(
                    $"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' couldn't be confirmed.")
                : ResultWrapper<Keccak>.Success(assetId);

        public async Task<ResultWrapper<Keccak>> ndm_rejectDepositApproval(Keccak assetId, Address consumer)
            => await _providerService.RejectDepositApprovalAsync(assetId, consumer) is null
                ? ResultWrapper<Keccak>.Fail(
                    $"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' couldn't be rejected.")
                : ResultWrapper<Keccak>.Success(assetId);

        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_updatePaymentClaimGasPrice(
            Keccak paymentClaimId, UInt256 gasPrice)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(new UpdatedTransactionInfoForRpc(
                await _providerTransactionsService.UpdatePaymentClaimGasPriceAsync(paymentClaimId, gasPrice)));
        
        public async Task<ResultWrapper<bool>> ndm_setPaymentClaimGasPrice(UInt256 gasPrice)
        {
            await _gasPriceService.SetPaymentClaimGasPriceAsync(gasPrice);

            return ResultWrapper<bool>.Success(true);
        }
        
        public async Task<ResultWrapper<UInt256>> ndm_getPaymentClaimGasPrice()
            => ResultWrapper<UInt256>.Success(await _gasPriceService.GetCurrentPaymentClaimGasPriceAsync());

        public async Task<ResultWrapper<UpdatedTransactionInfoForRpc>> ndm_cancelPaymentClaim(Keccak paymentClaimId)
            => ResultWrapper<UpdatedTransactionInfoForRpc>.Success(new UpdatedTransactionInfoForRpc(
                await _providerTransactionsService.CancelPaymentClaimAsync(paymentClaimId)));

        public async Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getProviderPendingTransactions()
        {
            var transactions = await _providerTransactionsService.GetPendingAsync();

            return ResultWrapper<IEnumerable<ResourceTransactionForRpc>>.Success(transactions
                .Select(t => new ResourceTransactionForRpc(t)));
        }

        public async Task<ResultWrapper<IEnumerable<ResourceTransactionForRpc>>> ndm_getAllProviderTransactions()
        {
            var transactions = await _providerTransactionsService.GetAllTransactionsAsync();

            return ResultWrapper<IEnumerable<ResourceTransactionForRpc>>.Success(transactions
                .Select(t => new ResourceTransactionForRpc(t)));
        }
        
        public ResultWrapper<GasLimitsForRpc> ndm_getProviderGasLimits()
            => ResultWrapper<GasLimitsForRpc>.Success(new GasLimitsForRpc(_gasLimitsService.GasLimits));

        public async Task<ResultWrapper<bool>> ndm_setReceiptRequestThreshold(UInt256 value)
        {
            await _providerThresholdsService.SetReceiptRequestAsync(value);

            return ResultWrapper<bool>.Success(true);
        }

        public async Task<ResultWrapper<UInt256>> ndm_getReceiptRequestThreshold()
            => ResultWrapper<UInt256>.Success(await _providerThresholdsService.GetCurrentReceiptRequestAsync());

        public async Task<ResultWrapper<bool>> ndm_setReceiptsMergeThreshold(UInt256 value)
        {
            await _providerThresholdsService.SetReceiptsMergeAsync(value);

            return ResultWrapper<bool>.Success(true);
        }

        public async Task<ResultWrapper<UInt256>> ndm_getReceiptsMergeThreshold()
            => ResultWrapper<UInt256>.Success(await _providerThresholdsService.GetCurrentReceiptsMergeAsync());

        public async Task<ResultWrapper<bool>> ndm_setPaymentClaimThreshold(UInt256 value)
        {
            await _providerThresholdsService.SetPaymentClaimAsync(value);

            return ResultWrapper<bool>.Success(true);
        }

        public async Task<ResultWrapper<UInt256>> ndm_getPaymentClaimThreshold()
            => ResultWrapper<UInt256>.Success(await _providerThresholdsService.GetCurrentPaymentClaimAsync());
    }
}