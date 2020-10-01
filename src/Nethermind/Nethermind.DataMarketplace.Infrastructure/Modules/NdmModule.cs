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

using System.IO;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Modules
{
    public class NdmModule : INdmModule
    {
        public void Init(INdmApi api)
        {
            AddDecoders();
            var config = api.NdmConfig;
            api.ConsumerAddress = string.IsNullOrWhiteSpace(config.ConsumerAddress)
                ? Address.Zero
                : new Address(config.ConsumerAddress);
            api.ContractAddress = string.IsNullOrWhiteSpace(config.ContractAddress)
                ? Address.Zero
                : new Address(config.ContractAddress);

            if (config.ProxyEnabled)
            {
                if (config.JsonRpcUrlProxies == null || api.EthJsonRpcClientProxy == null)
                {
                    throw new InvalidDataException("JSON RPC proxy is enabled but the proxies were not initialized properly.");
                }
                
                api.JsonRpcClientProxy!.SetUrls(config.JsonRpcUrlProxies!);
                api.BlockchainBridge = new NdmBlockchainBridgeProxy(
                    api.EthJsonRpcClientProxy);
            }
            else
            {
                api.BlockchainBridge = new NdmBlockchainBridge(
                    api.CreateBlockchainBridge(),
                    api.BlockTree,
                    api.StateReader,
                    api.TxSender);
            }

            api.GasPriceService
                = new GasPriceService(api.HttpClient, api.ConfigManager, config.Id, api.Timestamper, api.LogManager);
            api.TransactionService
                = new TransactionService(api.BlockchainBridge, api.Wallet, api.ConfigManager, config.Id, api.LogManager);
            api.DepositService
                = new DepositService(api.BlockchainBridge, api.AbiEncoder, api.Wallet, api.ContractAddress);
            api.JsonRpcNdmConsumerChannel
                = new JsonRpcNdmConsumerChannel(api.LogManager);
            
            if (config.JsonRpcDataChannelEnabled)
            {
                api.NdmConsumerChannelManager.Add(api.JsonRpcNdmConsumerChannel);
            }
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
