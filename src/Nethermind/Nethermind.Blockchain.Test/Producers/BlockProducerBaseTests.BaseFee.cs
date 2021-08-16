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

using System.IO;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    public partial class BlockProducerBaseTests
    {
        public static class BadContract
        {
            public static readonly AbiSignature Divide = new AbiSignature("divide"); // divide
        }
        
        public static class BaseFeeTestScenario
        {
            public class ScenarioBuilder
            {
                private Address _address = TestItem.Addresses[0];
                private Address _contractAddress;
                private IAbiEncoder _abiEncoder = new AbiEncoder();
                private long _eip1559TransitionBlock;
                private bool _eip1559Enabled;
                private TestRpcBlockchain _testRpcBlockchain;
                private Task<ScenarioBuilder> _antecedent;
                private UInt256 _currentNonce = 1;

                public ScenarioBuilder WithEip1559TransitionBlock(long transitionBlock)
                {
                    _eip1559Enabled = true;
                    _eip1559TransitionBlock = transitionBlock;
                    return this;
                }

                private async Task<ScenarioBuilder> CreateTestBlockchainAsync(long gasLimit)
                {
                    await ExecuteAntecedentIfNeeded();
                    SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(
                        new ReleaseSpec()
                        {
                            IsEip1559Enabled = _eip1559Enabled, Eip1559TransitionBlock = _eip1559TransitionBlock
                        }, 1);
                    BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(gasLimit);
                    _testRpcBlockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                        .WithGenesisBlockBuilder(blockBuilder)
                        .Build(spec);
                    _testRpcBlockchain.TestWallet.UnlockAccount(_address, new SecureString());
                    await _testRpcBlockchain.AddFunds(_address, 1.Ether());
                    return this;
                }

                public ScenarioBuilder CreateTestBlockchain(long gasLimit = 10000000000)
                {
                    _antecedent = CreateTestBlockchainAsync(gasLimit);
                    return this;
                }

                public ScenarioBuilder DeployContract()
                {
                    _antecedent = DeployContractAsync();
                    return this;
                }

                private async Task<ScenarioBuilder> DeployContractAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    _contractAddress = ContractAddress.From(_address, 0L);
                    byte[] bytecode = await GetContractBytecode("BadContract");
                    Transaction tx = new Transaction();
                    tx.Value = 0;
                    tx.Data = bytecode;
                    tx.GasLimit = 1000000;
                    tx.GasPrice = 20.GWei();
                    tx.SenderAddress = _address;
                    await _testRpcBlockchain.TxSender.SendTransaction(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
                    return this;
                }
                
                public ScenarioBuilder SendEip1559Transaction(long gasLimit = 1000000, UInt256? gasPremium = null, UInt256? feeCap = null)
                {
                    _antecedent = SendTransactionAsync(gasLimit, gasPremium ?? 20.GWei(), feeCap ?? UInt256.Zero);
                    return this;
                }
                
                public ScenarioBuilder SendLegacyTransaction(long gasLimit = 1000000, UInt256? gasPremium = null)
                {
                    _antecedent = SendTransactionAsync(gasLimit, gasPremium ?? 20.GWei(), UInt256.Zero);
                    return this;
                }
                private async Task<ScenarioBuilder> SendTransactionAsync(long gasLimit, UInt256 gasPrice, UInt256 feeCap)
                {
                    await ExecuteAntecedentIfNeeded();
                    byte[] txData = _abiEncoder.Encode(
                        AbiEncodingStyle.IncludeSignature,
                        BadContract.Divide);
                    Transaction tx = new Transaction();
                    tx.Value = 0;
                    tx.Data = txData;
                    tx.To = _contractAddress;
                    tx.SenderAddress = _address;
                    tx.GasLimit = gasLimit;
                    tx.GasPrice = gasPrice;
                    tx.DecodedMaxFeePerGas = feeCap;
                    tx.Nonce = _currentNonce;
                    ++_currentNonce;
                    
                    await _testRpcBlockchain.TxSender.SendTransaction(tx, TxHandlingOptions.None);
                    return this;
                }

                public ScenarioBuilder BlocksBeforeTransitionShouldHaveZeroBaseFee()
                {
                    _antecedent = BlocksBeforeTransitionShouldHaveZeroBaseFeeAsync();
                    return this;
                }

                public ScenarioBuilder AssertNewBlock(UInt256 expectedBaseFee, params Transaction[] transactions)
                {
                    _antecedent = AssertNewBlockAsync(expectedBaseFee, transactions);
                    return this;
                }
                
                public ScenarioBuilder AssertNewBlockWithDecreasedBaseFee()
                {
                    _antecedent = AssertNewBlockWithDecreasedBaseFeeAsync();
                    return this;
                }
                
                public ScenarioBuilder AssertNewBlockWithIncreasedBaseFee()
                {
                    _antecedent = AssertNewBlockWithIncreasedBaseFeeAsync();
                    return this;
                }

                private async Task<ScenarioBuilder> BlocksBeforeTransitionShouldHaveZeroBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block startingBlock = blockTree.Head;
                    Assert.AreEqual(UInt256.Zero, startingBlock!.Header.BaseFeePerGas);
                    for (long i = startingBlock.Number; i < _eip1559TransitionBlock - 1; ++i)
                    {
                        await _testRpcBlockchain.AddBlock();
                        Block currentBlock = blockTree.Head;
                        Assert.AreEqual(UInt256.Zero, currentBlock!.Header.BaseFeePerGas);
                    }

                    return this;
                }

                private async Task<ScenarioBuilder> AssertNewBlockAsync(UInt256 expectedBaseFee,
                    params Transaction[] transactions)
                {
                    await ExecuteAntecedentIfNeeded();
                    await _testRpcBlockchain.AddBlock(transactions);
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block headBlock = blockTree.Head;
                    Assert.AreEqual(expectedBaseFee, headBlock!.Header.BaseFeePerGas);

                    return this;
                }
                
                private async Task<ScenarioBuilder> AssertNewBlockWithDecreasedBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block startingBlock = blockTree.Head;
                    await _testRpcBlockchain.AddBlock();
                    Block newBlock = blockTree.Head;
                    Assert.Less(newBlock!.Header.BaseFeePerGas, startingBlock!.Header.BaseFeePerGas);

                    return this;
                }
                
                private async Task<ScenarioBuilder> AssertNewBlockWithIncreasedBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();
                    
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block startingBlock = blockTree.Head;
                    await _testRpcBlockchain.AddBlock();
                    Block newBlock = blockTree.Head;
                    Assert.Less(startingBlock!.Header.BaseFeePerGas, newBlock!.Header.BaseFeePerGas);

                    return this;
                }

                private async Task ExecuteAntecedentIfNeeded()
                {
                    if (_antecedent != null)
                        await _antecedent;
                }

                public async Task Finish()
                {
                    await ExecuteAntecedentIfNeeded();
                }
                
                private async Task<byte[]> GetContractBytecode(string contract)
                {
                    string[] contractBytecode = await File.ReadAllLinesAsync($"contracts/{contract}.bin");
                    if (contractBytecode.Length < 4)
                    {
                        throw new IOException("Bytecode not found");
                    }

                    string bytecodeHex = contractBytecode[3];
                    return Bytes.FromHexString(bytecodeHex);
                }
            }

            public static ScenarioBuilder GoesLikeThis()
            {
                return new ScenarioBuilder();
            }
        }

        [Test]
        public async Task BlockProducer_has_blocks_with_zero_base_fee_before_fork()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(5)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee();
            await scenario.Finish();
        }

        [Test]
        public async Task BlockProducer_returns_correct_fork_base_fee()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(7)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee);
            await scenario.Finish();
        }

        [Test]
        public async Task BlockProducer_returns_correctly_decreases_base_fee_on_empty_blocks()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee)
                .AssertNewBlock(875000000)
                .AssertNewBlock(765625000)
                .AssertNewBlock(669921875)
                .AssertNewBlock(586181641);
            await scenario.Finish();
        }

        [Test]
        public async Task BaseFee_should_decrease_when_we_send_transactions_below_gas_target()
        {
            long gasLimit = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .CreateTestBlockchain(gasLimit)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee)
                .SendLegacyTransaction(gasLimit / 3, 20.GWei())
                .SendEip1559Transaction(gasLimit / 3, 1.GWei(), 20.GWei())
                .AssertNewBlock(875000000)
                .AssertNewBlockWithDecreasedBaseFee()
                .AssertNewBlockWithDecreasedBaseFee();
            await scenario.Finish();
        }
        
        [Test]
        public async Task BaseFee_should_not_change_when_we_send_transactions_equal_gas_target()
        {
            long gasTarget = 3000000;
            long gasLimit = Eip1559Constants.ElasticityMultiplier * gasTarget;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .CreateTestBlockchain(gasTarget)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee)
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
                .AssertNewBlock(875000000)
                .AssertNewBlock(875000000)
                .AssertNewBlockWithDecreasedBaseFee();
            await scenario.Finish();
        }
        
        [Test]
        public async Task BaseFee_should_increase_when_we_send_transactions_above_gas_target()
        {
            long gasTarget = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .CreateTestBlockchain(gasTarget)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee)
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .AssertNewBlock(875000000)
                .AssertNewBlockWithIncreasedBaseFee()
                .AssertNewBlockWithDecreasedBaseFee();
            await scenario.Finish();
        }
    }
}
