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
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Modules
{
    public class NdmModule : INdmModule
    {
        private readonly INdmApi _api;
        public NdmModule(INdmApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api)); 
        }

        public Task InitAsync()
        {
            AddDecoders();
            var config = _api.NdmConfig;
            _api.ConsumerAddress = string.IsNullOrWhiteSpace(config.ConsumerAddress)
                ? Address.Zero
                : new Address(config.ConsumerAddress);
            _api.ContractAddress = string.IsNullOrWhiteSpace(config.ContractAddress)
                ? Address.Zero
                : new Address(config.ContractAddress);

            if (config.ProxyEnabled)
            {
                if (config.JsonRpcUrlProxies == null || _api.EthJsonRpcClientProxy == null)
                {
                    throw new InvalidDataException("JSON RPC proxy is enabled but the proxies were not initialized properly.");
                }
                
                _api.JsonRpcClientProxy!.SetUrls(config.JsonRpcUrlProxies!);
                _api.BlockchainBridge = new NdmBlockchainBridgeProxy(
                    _api.EthJsonRpcClientProxy);
            }
            else
            {
                _api.BlockchainBridge = new NdmBlockchainBridge(
                    _api.CreateBlockchainBridge(),
                    _api.BlockTree,
                    _api.StateReader,
                    _api.TxSender);
            }

            _api.GasPriceService
                = new GasPriceService(_api.HttpClient, _api.ConfigManager, config.Id, _api.Timestamper, _api.LogManager);
            _api.TransactionService
                = new TransactionService(_api.BlockchainBridge, _api.Wallet, _api.ConfigManager, config.Id, _api.LogManager);
            _api.DepositService
                = new DepositService(_api.BlockchainBridge, _api.AbiEncoder, _api.Wallet, _api.ContractAddress);
            _api.JsonRpcNdmConsumerChannel
                = new JsonRpcNdmConsumerChannel(_api.LogManager);
            
            if (config.JsonRpcDataChannelEnabled)
            {
                _api.NdmConsumerChannelManager.Add(_api.JsonRpcNdmConsumerChannel);
            }

            return Task.CompletedTask;
        }

        private static void AddDecoders()
        {
            DataDeliveryReceiptDecoder.Init();
            DataDeliveryReceiptRequestDecoder.Init();
            DataDeliveryReceiptToMergeDecoder.Init();
            DataDeliveryReceiptDetailsDecoder.Init();
            DataAssetDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetProviderDecoder.Init();
            DataRequestDecoder.Init();
            DepositDecoder.Init();
            DepositApprovalDecoder.Init();
            EarlyRefundTicketDecoder.Init();
            EthRequestDecoder.Init();
            FaucetResponseDecoder.Init();
            FaucetRequestDetailsDecoder.Init();
            SessionDecoder.Init();
            TransactionInfoDecoder.Init();
            UnitsRangeDecoder.Init();
        }
    }
}
