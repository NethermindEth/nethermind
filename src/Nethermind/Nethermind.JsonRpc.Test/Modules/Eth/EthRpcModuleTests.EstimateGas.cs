//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    [TestFixture]
    public partial class EthRpcModuleTests
    {
        [Test]
        public async Task Eth_estimateGas_web3_should_return_insufficient_balance_error()
        {
            using Context ctx = await Context.Create();
            Address someAccount = new("0x0001020304050607080910111213141516171819");
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"value\": 500}");
            string serialized =
                ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"insufficient funds for transfer: address 0x0001020304050607080910111213141516171819\"},\"id\":67}",
                serialized);
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        }


        [Test]
        public async Task Eth_estimateGas_web3_sample_not_enough_gas_system_account()
        {
            using Context ctx = await Context.Create();
            ctx._test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}", serialized);
            ctx._test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
        }

        [Test]
        public async Task Eth_estimateGas_web3_sample_not_enough_gas_other_account()
        {
            using Context ctx = await Context.Create();
            Address someAccount = new("0x0001020304050607080910111213141516171819");
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}", serialized);
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public async Task Eth_estimateGas_web3_above_block_gas_limit()
        {
            using Context ctx = await Context.Create();
            Address someAccount = new("0x0001020304050607080910111213141516171819");
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gas\":\"0x100000000\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x53b8\",\"id\":67}", serialized);
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        }

        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(true, AccessTxTracer.MaxStorageAccessToOptimize + 5)]
        public async Task Eth_create_access_list_calculates_proper_gas(bool optimize, long loads)
        {
            var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(new TestSpecProvider(Berlin.Instance));

            (byte[] code, AccessListItemForRpc[] accessList) = GetTestAccessList(loads);

            TransactionForRpc transaction =
                test.JsonSerializer.Deserialize<TransactionForRpc>(
                    $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
            string serializedCreateAccessList = test.TestEthRpc("eth_createAccessList",
                test.JsonSerializer.Serialize(transaction), "0x0", optimize.ToString().ToLower());

            if (optimize && loads <= AccessTxTracer.MaxStorageAccessToOptimize)
            {
                accessList = GetTestAccessList(loads, false).AccessList;
            }

            transaction.AccessList = accessList;
            string serializedEstimateGas =
                test.TestEthRpc("eth_estimateGas", test.JsonSerializer.Serialize(transaction), "0x0");

            string gasUsedEstimateGas = JToken.Parse(serializedEstimateGas).Value<string>("result");
            string gasUsedCreateAccessList =
                JToken.Parse(serializedCreateAccessList).SelectToken("result.gasUsed").Value<string>();
            gasUsedCreateAccessList.Should().Be(gasUsedEstimateGas);
        }

        [TestCase(true, 0xeee7, 0xf71b)]
        [TestCase(false, 0xeee7, 0xee83)]
        public async Task Eth_estimate_gas_with_accessList(bool senderAccessList, long gasPriceWithoutAccessList,
            long gasPriceWithAccessList)
        {
            var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(new TestSpecProvider(Berlin.Instance));

            (byte[] code, AccessListItemForRpc[] accessList) = GetTestAccessList(2, senderAccessList);

            TransactionForRpc transaction =
                test.JsonSerializer.Deserialize<TransactionForRpc>(
                    $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
            string serialized = test.TestEthRpc("eth_estimateGas", test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual(
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{gasPriceWithoutAccessList.ToHexString(true)}\",\"id\":67}}",
                serialized);

            transaction.AccessList = accessList;
            serialized = test.TestEthRpc("eth_estimateGas", test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual(
                $"{{\"jsonrpc\":\"2.0\",\"result\":\"{gasPriceWithAccessList.ToHexString(true)}\",\"id\":67}}",
                serialized);
        }

        [Test]
        public async Task Eth_estimate_gas_is_lower_with_optimized_access_list()
        {
            var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(new TestSpecProvider(Berlin.Instance));

            (byte[] code, AccessListItemForRpc[] accessList) = GetTestAccessList(2, true);
            (byte[] _, AccessListItemForRpc[] optimizedAccessList) = GetTestAccessList(2, false);

            TransactionForRpc transaction =
                test.JsonSerializer.Deserialize<TransactionForRpc>(
                    $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");
            transaction.AccessList = accessList;
            string serialized = test.TestEthRpc("eth_estimateGas", test.JsonSerializer.Serialize(transaction), "0x0");
            long estimateGas = Convert.ToInt64(JToken.Parse(serialized).Value<string>("result"), 16);

            transaction.AccessList = optimizedAccessList;
            serialized = test.TestEthRpc("eth_estimateGas", test.JsonSerializer.Serialize(transaction), "0x0");
            long optimizedEstimateGas = Convert.ToInt64(JToken.Parse(serialized).Value<string>("result"), 16);

            optimizedEstimateGas.Should().BeLessThan(estimateGas);
        }
        
        [Test]
        public async Task Estimate_gas_without_gas_pricing()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Estimate_gas_with_gas_pricing()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
            string serialized = ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Estimate_gas_without_gas_pricing_after_1559_legacy()
        {
            using Context ctx = await Context.CreateWithLondonEnabled();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
            string serialized = ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Estimate_gas_without_gas_pricing_after_1559_new_type_of_transaction()
        {
            using Context ctx = await Context.CreateWithLondonEnabled();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\"}");
            string serialized = ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5208\",\"id\":67}", serialized);
            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
        }

        [Test]
        public async Task Estimate_gas_with_base_fee_opcode()
        {
            using Context ctx = await Context.CreateWithLondonEnabled();

            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData("0x20")
                .PushData("0x0")
                .Op(Instruction.RETURN)
                .Done;

            string dataStr = code.ToHexString();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                $"{{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\", \"data\": \"{dataStr}\"}}");
            string serialized = ctx._test.TestEthRpc("eth_estimateGas", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":\"0xe891\",\"id\":67}",
                serialized);
        }
    }
}
