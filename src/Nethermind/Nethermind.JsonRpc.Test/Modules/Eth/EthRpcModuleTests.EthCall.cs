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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    [TestFixture]
    public partial class EthRpcModuleTests
    {
        [Test]
        public async Task Eth_call_web3_sample()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_web3_sample_not_enough_gas_system_account()
        {
            using Context ctx = await Context.Create();
            ctx._test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
            ctx._test.ReadOnlyState.AccountExists(Address.SystemUser).Should().BeFalse();
        }

        [Test]
        public async Task Eth_call_web3_should_return_insufficient_balance_error()
        {
            using Context ctx = await Context.Create();
            Address someAccount = new Address("0x0001020304050607080910111213141516171819");
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"value\": 500}");
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"insufficient funds for transfer: address 0x0001020304050607080910111213141516171819\"},\"id\":67}",
                serialized);
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public async Task Eth_call_web3_sample_not_enough_gas_other_account()
        {
            using Context ctx = await Context.Create();
            Address someAccount = new Address("0x0001020304050607080910111213141516171819");
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
            ctx._test.ReadOnlyState.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public async Task Eth_call_no_sender()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.To = TestItem.AddressB;

            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_no_recipient_should_work_as_init()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.Data = new byte[] {1, 2, 3};

            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32015,\"message\":\"VM execution error.\",\"data\":\"StackUnderflow\"},\"id\":67}",
                serialized);
        }

        [Test]
        public async Task Eth_call_ethereum_recipient()
        {
            using Context ctx = await Context.Create();
            string serialized = ctx._test.TestEthRpc("eth_call",
                "{\"data\":\"0x12\",\"from\":\"0x7301cfa0e1756b71869e93d4e4dca5c7d0eb0aa6\",\"to\":\"ethereum\"}",
                "latest");
            Assert.True(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""));
        }

        [Test]
        public async Task Eth_call_ok()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.To = TestItem.AddressB;

            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_missing_state_after_fast_sync()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.To = TestItem.AddressB;

            ctx._test.StateDb.Clear();
            ctx._test.TrieStore.ClearCache();

            string serialized =
                ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction), "latest");
            serialized.Should().StartWith("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,");
        }

        [Test]
        public async Task Eth_call_with_accessList()
        {
            var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .Build(new TestSpecProvider(Berlin.Instance));

            (byte[] code, AccessListItemForRpc[] accessList) = GetTestAccessList();

            TransactionForRpc transaction =
                test.JsonSerializer.Deserialize<TransactionForRpc>(
                    $"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");

            transaction.AccessList = accessList;
            string serialized = test.TestEthRpc("eth_call", test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x010203\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_without_gas_pricing()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_with_gas_pricing()
        {
            using Context ctx = await Context.Create();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_without_gas_pricing_after_1559_legacy()
        {
            using Context ctx = await Context.CreateWithLondonEnabled();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"gasPrice\": \"0x10\"}");
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_call_without_gas_pricing_after_1559_new_type_of_transaction()
        {
            using Context ctx = await Context.CreateWithLondonEnabled();
            TransactionForRpc transaction = ctx._test.JsonSerializer.Deserialize<TransactionForRpc>(
                "{\"from\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"to\": \"0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24\", \"type\": \"0x2\"}");
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
        }

        [Test]
        public async Task Eth_call_with_base_fee_opcode()
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
            string serialized = ctx._test.TestEthRpc("eth_call", ctx._test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":\"0x000000000000000000000000000000000000000000000000000000002da282a8\",\"id\":67}",
                serialized);
        }
    }
}
