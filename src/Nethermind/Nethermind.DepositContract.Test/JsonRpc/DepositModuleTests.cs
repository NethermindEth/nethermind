using System.Buffers.Binary;
using System.IO;
using System.IO.Abstractions;
using System.Numerics;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DepositContract.Test.JsonRpc
{
    [TestFixture]
    public class DepositModuleTests
    {
        [Test]
        public async Task deploy_deploys_the_contract()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            
            DepositModule depositModule = new DepositModule(
                testRpc.TxPoolBridge,
                testRpc.LogFinder,
                new DepositConfig() {DepositContractAddress = TestItem.AddressA.ToString()},
                LimboLogs.Instance);
            
            var result = await depositModule.deposit_deploy(TestItem.Addresses[0]);
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
            
            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }
        
        [Test]
        public async Task can_deposit_32eth()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 33.Ether());
            
            DepositModule depositModule = new DepositModule(
                testRpc.TxPoolBridge,
                testRpc.LogFinder,
                new DepositConfig(),
                LimboLogs.Instance);
            
            await depositModule.deposit_deploy(TestItem.Addresses[0]);
            await testRpc.AddBlock();

            var contractAddress = ContractAddress.From(TestItem.Addresses[0], 0);
            await depositModule.deposit_setContractAddress(contractAddress);
            
            var result = await depositModule.deposit_make(
                TestItem.Addresses[0],
                new byte[48],
                new byte[32],
                new byte[96]);
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
            
            await testRpc.AddBlock();
            
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => !tx.IsContractCreation);
            
            /* if status is 1 then it means that no revert has been made */
            testRpc.ReceiptStorage.Get(testRpc.BlockTree.Head)[0].StatusCode.Should().Be(1);
        }
        
        [Test]
        public async Task can_getLogs()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 33.Ether());
            
            DepositModule depositModule = new DepositModule(
                testRpc.TxPoolBridge,
                testRpc.LogFinder,
                new DepositConfig() {DepositContractAddress = TestItem.AddressA.ToString()},
                LimboLogs.Instance);
            
            await depositModule.deposit_deploy(TestItem.Addresses[0]);
            await testRpc.AddBlock();

            var contractAddress = ContractAddress.From(TestItem.Addresses[0], 0);
            await depositModule.deposit_setContractAddress(contractAddress);
            
            await depositModule.deposit_make(
                TestItem.Addresses[0],
                GetNonZeroBytes(48),
                GetNonZeroBytes(32),
                GetNonZeroBytes(96));

            await testRpc.AddBlock();

            var all = depositModule.deposit_getAll();
            all.Result.Data.Length.Should().Be(1);
            ulong amount = BinaryPrimitives.ReadUInt64LittleEndian(all.Result.Data[0].Amount);
            ((BigInteger) amount).Should().Be((BigInteger) 32.Ether() / (BigInteger) 1.GWei());
        }

        private byte[] GetNonZeroBytes(int length)
        {
            byte[] result = new byte[length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte) i;
            }
            return result;
        }
    }
}
