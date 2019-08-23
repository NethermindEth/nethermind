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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Personal;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc
{
    public class NdmRpcConsumerModule : INdmRpcConsumerModule
    {

        private readonly IConsumerService _consumerService;
        private readonly IDepositReportService _depositReportService;
        private readonly IJsonRpcNdmConsumerChannel _jsonRpcNdmConsumerChannel;
        private readonly IEthRequestService _ethRequestService;
        private readonly IPersonalBridge _personalBridge;
        private readonly ITimestamper _timestamper;

        public NdmRpcConsumerModule(IConsumerService consumerService, IDepositReportService depositReportService,
            IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel, IEthRequestService ethRequestService,
            IPersonalBridge personalBridge, ITimestamper timestamper)
        {
            _consumerService = consumerService;
            _depositReportService = depositReportService;
            _jsonRpcNdmConsumerChannel = jsonRpcNdmConsumerChannel;
            _ethRequestService = ethRequestService;
            _personalBridge = personalBridge;
            _timestamper = timestamper;
        }

        public ResultWrapper<AccountForRpc[]> ndm_listAccounts()
        {
            if (_personalBridge is null)
            {
                return ResultWrapper<AccountForRpc[]>.Success(Array.Empty<AccountForRpc>());
            }

            var accounts = _personalBridge.ListAccounts().Select(a => new AccountForRpc
            {
                Address = a,
                Unlocked = _personalBridge.IsUnlocked(a)
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
            var dataAssets = await _consumerService.GetKnownDataAssetsAsync();

            return ResultWrapper<DataAssetInfoForRpc[]>.Success(dataAssets
                .Select(d => new DataAssetInfoForRpc(d)).ToArray());
        }

        public async Task<ResultWrapper<ProviderInfoForRpc[]>> ndm_getKnownProviders()
        {
            var providers = await _consumerService.GetKnownProvidersAsync();

            return ResultWrapper<ProviderInfoForRpc[]>.Success(providers
                .Select(p => new ProviderInfoForRpc(p)).ToArray());
        }

        public ResultWrapper<Address[]> ndm_getConnectedProviders()
            => ResultWrapper<Address[]>.Success(_consumerService.GetConnectedProviders().ToArray());

        public ResultWrapper<ConsumerSessionForRpc[]> ndm_getActiveConsumerSessions()
            => ResultWrapper<ConsumerSessionForRpc[]>.Success(_consumerService.GetActiveSessions()
                .Select(s => new ConsumerSessionForRpc(s)).ToArray());

        public async Task<ResultWrapper<PagedResult<DepositDetailsForRpc>>> ndm_getDeposits(GetDeposits query)
        {
            var timestamp = (uint) _timestamper.EpochSeconds;
            var deposits = await _consumerService.GetDepositsAsync(query ?? new GetDeposits
            {
                Results = int.MaxValue
            });

            return ResultWrapper<PagedResult<DepositDetailsForRpc>>.Success(PagedResult<DepositDetailsForRpc>.From(
                deposits, deposits.Items.Select(d => new DepositDetailsForRpc(d, timestamp)).ToArray()));
        }

        public async Task<ResultWrapper<DepositDetailsForRpc>> ndm_getDeposit(Keccak depositId)
        {
            var timestamp = (uint) _timestamper.EpochSeconds;
            var deposit = await _consumerService.GetDepositAsync(depositId);

            return deposit == null
                ? ResultWrapper<DepositDetailsForRpc>.Fail($"Deposit: '{depositId}' was not found.")
                : ResultWrapper<DepositDetailsForRpc>.Success(new DepositDetailsForRpc(deposit, timestamp));
        }

        public async Task<ResultWrapper<Keccak>> ndm_makeDeposit(MakeDepositForRpc deposit)
        {
            var depositId = await _consumerService.MakeDepositAsync(deposit.DataAssetId, deposit.Units, deposit.Value);

            return depositId is null
                ? ResultWrapper<Keccak>.Fail("Deposit couldn't be made.")
                : ResultWrapper<Keccak>.Success(depositId);
        }

        public async Task<ResultWrapper<string>> ndm_sendDataRequest(Keccak depositId)
        {
            var result = await _consumerService.SendDataRequestAsync(depositId);
            return ResultWrapper<string>.Success(result.ToString());
        }

        public async Task<ResultWrapper<Keccak>> ndm_finishSession(Keccak depositId)
            => await _consumerService.SendFinishSessionAsync(depositId) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't finish session for deposit: '{depositId}'.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_enableDataStream(Keccak depositId, string client, string[] args)
            => await _consumerService.EnableDataStreamAsync(depositId, client, args) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't enable data stream for deposit: '{depositId}', client: {client}.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_disableDataStream(Keccak depositId, string client)
            => await _consumerService.DisableDataStreamAsync(depositId, client) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't disable data stream for deposit: '{depositId}', client: {client}.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<Keccak>> ndm_disableDataStreams(Keccak depositId)
            => await _consumerService.DisableDataStreamsAsync(depositId) is null
                ? ResultWrapper<Keccak>.Fail($"Couldn't disable data streams for deposit: '{depositId}'.")
                : ResultWrapper<Keccak>.Success(depositId);

        public async Task<ResultWrapper<DepositsReportForRpc>> ndm_getDepositsReport(GetDepositsReport query = null)
        {
            var report = await _depositReportService.GetAsync(query ?? new GetDepositsReport());

            return ResultWrapper<DepositsReportForRpc>.Success(new DepositsReportForRpc(report));
        }

        public async Task<ResultWrapper<PagedResult<DepositApprovalForRpc>>> ndm_getConsumerDepositApprovals(
            GetConsumerDepositApprovals query = null)
        {
            var depositApprovals = await _consumerService.GetDepositApprovalsAsync(
                query ?? new GetConsumerDepositApprovals
                {
                    Results = int.MaxValue
                });

            return ResultWrapper<PagedResult<DepositApprovalForRpc>>.Success(PagedResult<DepositApprovalForRpc>.From(
                depositApprovals, depositApprovals.Items.Select(d => new DepositApprovalForRpc(d)).ToArray()));
        }

        public async Task<ResultWrapper<Keccak>> ndm_requestDepositApproval(Keccak assetId, string kyc)
        {
            var id = await _consumerService.RequestDepositApprovalAsync(assetId, kyc);

            return id is null
                ? ResultWrapper<Keccak>.Fail($"Deposit approval for data asset: '{assetId} couldn't be requested.")
                : ResultWrapper<Keccak>.Success(id);
        }

        public async Task<ResultWrapper<FaucetResponseForRpc>> ndm_requestEth(Address address)
        {
            var response = await _ethRequestService.TryRequestEthAsync(address, 1.Ether());

            return ResultWrapper<FaucetResponseForRpc>.Success(new FaucetResponseForRpc(response));
        }

        public ResultWrapper<string> ndm_pullData(Keccak depositId)
        {
            var data = _jsonRpcNdmConsumerChannel.Pull(depositId);

            return ResultWrapper<string>.Success(data);
        }
    }
}