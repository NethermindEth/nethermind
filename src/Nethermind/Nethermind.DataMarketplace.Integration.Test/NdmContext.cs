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
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Integration.Test.JsonRpc.Dto;
using Nethermind.JsonRpc.Data;
using Nethermind.Overseer.Test.Framework;
using Nethermind.Overseer.Test.JsonRpc;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Integration.Test
{
    public class NdmContext : TestContextBase<NdmContext, NdmState>
    {
        private const string DataConsumerName = "dataConsumer";
        private const string DataProviderName = "dataProvider";

        public NdmContext(NdmState state) : base(state)
        {
        }
        
        public NdmContext DC
        {
            get
            {
                TestBuilder.SwitchNode(DataConsumerName);
                return this;
            }
        }

        public NdmContext DP
        {
            get
            {
                TestBuilder.SwitchNode(DataProviderName);
                return this;
            }
        }

        public T SwitchContext<T>(T testContext) where T : ITestContext
        {
            return LeaveContext().SetContext(testContext);
        }

        public NdmContext AddDataAsset(Func<DataAssetDto> dataAsset, string name = "Add data asset",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_addDataAsset",
                () => client.PostAsync<string>(nameof(AddDataAsset), new object[] {dataAsset()}), validator, stateUpdater);
        }

        public NdmContext GetDataAssets(string name = "Get data assets",
            Func<DataAssetDto[], bool> validator = null,
            Action<NdmState, JsonRpcResponse<DataAssetDto[]>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_getDataAssets",
                () => client.PostAsync<DataAssetDto[]>(nameof(GetDataAssets)), validator, stateUpdater);
        }

        public NdmContext GetDiscoveredDataAssets(string name = "Get discovered data assets",
            Func<DataAssetDto[], bool> validator = null,
            Action<NdmState, JsonRpcResponse<DataAssetDto[]>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_getDiscoveredDataAssets",
                () => client.PostAsync<DataAssetDto[]>(nameof(GetDiscoveredDataAssets)), validator, stateUpdater);
        }

        public NdmContext GetDeposits(string name = "Get deposits",
            Func<DepositDetailsDto[], bool> validator = null,
            Action<NdmState, JsonRpcResponse<DepositDetailsDto[]>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_getDeposits",
                () => client.PostAsync<DepositDetailsDto[]>(nameof(GetDeposits)), validator, stateUpdater);
        }

        public NdmContext MakeDeposit(Func<MakeDepositDto> deposit, string name = "Make deposit",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_makeDeposit", () => client.PostAsync<string>(nameof(MakeDeposit), new object[] {deposit()}), validator, stateUpdater);
        }

        public NdmContext SendDataRequest(Func<string> depositId, string name = "Send data request",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_sendDataRequest",
                () => client.PostAsync<string>(nameof(SendDataRequest), new object[] {depositId()}), validator, stateUpdater);
        }


        public NdmContext EnableDataStream(Func<string> depositId, string[] args,
            string name = "Enable data stream", Func<string, bool> validator = null,
            Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_enableDataStream",
                () => client.PostAsync<string>(nameof(EnableDataStream), new object[] {depositId(), args}), validator, stateUpdater);
        }

        public NdmContext DisableDataStream(Func<string> depositId, string name = "Disable data stream",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_disableDataStream",
                () => client.PostAsync<string>(nameof(DisableDataStream), new object[] {depositId()}), validator, stateUpdater);
        }

        public NdmContext SendData(Func<DataAssetDataDto> data, string name = "Send data",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_sendData", () => client.PostAsync<string>(nameof(SendData), new object[] {data()}), validator, stateUpdater);
        }

        public NdmContext DeployNdmContract(string name = "Deploy contract")
        {
            TransactionForRpc deployContract = new TransactionForRpc();
            deployContract.From = new DevWallet(new WalletConfig(), LimboLogs.Instance).GetAccounts()[0];
            deployContract.Gas = 4000000;
            deployContract.Data = Bytes.FromHexString(File.ReadAllText("contractCode.txt"));
            deployContract.Value = 0;
            deployContract.GasPrice = 20.GWei();

            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "eth_sendTransaction",
                () => client.PostAsync<string>("eth_sendTransaction", new object[] {deployContract}));
        }

        public NdmContext PullData(Func<string> depositId, string name = "Pull data",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_pullData",
                () => client.PostAsync<string>(nameof(PullData), new object[] {depositId()}), validator, stateUpdater);
        }

        public NdmContext StartDataProvider(string name = DataProviderName)
        {
            TestBuilder.StartNode(name, "configs/ndm_provider.cfg");
            return this;
        }

        public NdmContext StartDataConsumer(string name = DataConsumerName)
        {
            TestBuilder.StartNode(name, "configs/ndm_consumer.cfg");
            return this;
        }

        public NdmContext KillDataProvider(string name = DataProviderName)
        {
            TestBuilder.SwitchNode(name);
            TestBuilder.Kill();
            return this;
        }

        public NdmContext KillDataConsumer(string name = DataConsumerName)
        {
            TestBuilder.SwitchNode(name);
            TestBuilder.Kill();
            return this;
        }
    }
}