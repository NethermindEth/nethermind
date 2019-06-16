using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.DataMarketplace.TestRunner.JsonRpc;
using Nethermind.DataMarketplace.TestRunner.JsonRpc.Dto;
using Nethermind.DataMarketplace.TestRunner.Tester;
using Nethermind.JsonRpc.Data;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.TestRunner.Framework
{
    public class NdmContext : TestContextBase<NdmContext, NdmState>
    {
        private const string DataConsumerName = "dataConsumer";
        private const string DataProviderName = "dataProvider";

        public NdmContext(ILogger<ITestContext> logger)
        {
            _logger = logger;
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

        public TestBuilder LeaveContext()
        {
            return TestBuilder;
        }

        public T SwitchContext<T>(T testContext) where T : ITestContext
        {
            return LeaveContext().SetContext(testContext);
        }

        public NdmContext AddDataHeader(Func<DataHeaderDto> dataHeader, string name = "Add data header",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_addDataHeader",
                () => client.PostAsync<string>(nameof(AddDataHeader), new object[] {dataHeader()}), validator, stateUpdater);
        }

        public NdmContext GetDataHeaders(string name = "Get data headers",
            Func<DataHeaderDto[], bool> validator = null,
            Action<NdmState, JsonRpcResponse<DataHeaderDto[]>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_getDataHeaders",
                () => client.PostAsync<DataHeaderDto[]>(nameof(GetDataHeaders)), validator, stateUpdater);
        }

        public NdmContext GetDiscoveredDataHeaders(string name = "Get discovered data headers",
            Func<DataHeaderDto[], bool> validator = null,
            Action<NdmState, JsonRpcResponse<DataHeaderDto[]>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_getDiscoveredDataHeaders",
                () => client.PostAsync<DataHeaderDto[]>(nameof(GetDiscoveredDataHeaders)), validator, stateUpdater);
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


        public NdmContext EnableDataStream(Func<string> depositId, string[] subscriptions,
            string name = "Enable data stream", Func<string, bool> validator = null,
            Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_enableDataStream",
                () => client.PostAsync<string>(nameof(EnableDataStream), new object[] {depositId(), subscriptions}), validator, stateUpdater);
        }

        public NdmContext DisableDataStream(Func<string> depositId, string name = "Disable data stream",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_disableDataStream",
                () => client.PostAsync<string>(nameof(DisableDataStream), new object[] {depositId()}), validator, stateUpdater);
        }

        public NdmContext SendData(Func<DataHeaderDataDto> data, string name = "Send data",
            Func<string, bool> validator = null, Action<NdmState, JsonRpcResponse<string>> stateUpdater = null)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "npm_sendData", () => client.PostAsync<string>(nameof(SendData), new object[] {data()}), validator, stateUpdater);
        }

        public NdmContext DeployNdmContract(string name = "Deploy contract")
        {
            Transaction deployContract = new Transaction();
            deployContract.SenderAddress = new DevWallet(LimboLogs.Instance).GetAccounts()[0];
            deployContract.GasLimit = 4000000;
            deployContract.Init = Bytes.FromHexString(File.ReadAllText("contractCode.txt"));
            deployContract.Nonce = 0;
            deployContract.Value = 0;
            deployContract.GasPrice = 20.GWei();

            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc(name, "eth_sendTransaction",
                () => client.PostAsync<string>("eth_sendTransaction", new object[] {new TransactionForRpc(null, null, null, deployContract)}));
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