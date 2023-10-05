// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
            public static AbiSignature Divide { get; } = new("divide"); // divide
        }

        public static partial class BaseFeeTestScenario
        {
            public partial class ScenarioBuilder
            {
                private readonly Address _address = TestItem.Addresses[0];
                private readonly IAbiEncoder _abiEncoder = new AbiEncoder();

                private Address _contractAddress = null!;
                private TestRpcBlockchain _testRpcBlockchain = null!;

                private long _eip1559TransitionBlock;
                private bool _eip1559Enabled;
                private Task<ScenarioBuilder>? _antecedent;
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
                    TestSingleReleaseSpecProvider spec = new(
                        new ReleaseSpec()
                        {
                            IsEip1559Enabled = _eip1559Enabled,
                            Eip1559TransitionBlock = _eip1559TransitionBlock,
                            Eip1559FeeCollector = _eip1559FeeCollector,
                            IsEip155Enabled = true
                        });
                    BlockBuilder blockBuilder = Build.A.Block.Genesis.WithGasLimit(gasLimit);
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
                    Transaction tx = new()
                    {
                        Value = 0,
                        Data = bytecode,
                        GasLimit = 1000000,
                        GasPrice = 20.GWei(),
                        SenderAddress = _address,
                    };
                    await _testRpcBlockchain.TxSender.SendTransaction(tx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
                    return this;
                }

                public ScenarioBuilder SendEip1559Transaction(long gasLimit = 1000000, UInt256? gasPremium = null, UInt256? feeCap = null, bool serviceTransaction = false)
                {
                    _antecedent = SendTransactionAsync(gasLimit, gasPremium ?? 20.GWei(), feeCap ?? UInt256.Zero, serviceTransaction);
                    return this;
                }

                public ScenarioBuilder SendLegacyTransaction(long gasLimit = 1000000, UInt256? gasPremium = null, bool serviceTransaction = false, UInt256? nonce = null)
                {
                    _antecedent = SendTransactionAsync(gasLimit, gasPremium ?? 20.GWei(), UInt256.Zero, serviceTransaction, nonce);
                    return this;
                }
                private async Task<ScenarioBuilder> SendTransactionAsync(long gasLimit, UInt256 gasPrice, UInt256 feeCap, bool serviceTransaction, UInt256? nonce = null)
                {
                    await ExecuteAntecedentIfNeeded();
                    byte[] txData = _abiEncoder.Encode(
                        AbiEncodingStyle.IncludeSignature,
                        BadContract.Divide);
                    Transaction tx = new()
                    {
                        Value = 0,
                        Data = txData,
                        To = _contractAddress,
                        SenderAddress = _address,
                        GasLimit = gasLimit,
                        GasPrice = gasPrice,
                        DecodedMaxFeePerGas = feeCap,
                        Nonce = nonce ?? _currentNonce++,
                        IsServiceTransaction = serviceTransaction
                    };

                    var (_, result) = await _testRpcBlockchain.TxSender.SendTransaction(tx, TxHandlingOptions.None);
                    Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
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
                    Block startingBlock = blockTree.Head!;
                    Assert.That(startingBlock.Header.BaseFeePerGas, Is.EqualTo(UInt256.Zero));
                    for (long i = startingBlock.Number; i < _eip1559TransitionBlock - 1; ++i)
                    {
                        await _testRpcBlockchain.AddBlock();
                        Block currentBlock = blockTree.Head!;
                        Assert.That(currentBlock.Header.BaseFeePerGas, Is.EqualTo(UInt256.Zero));
                    }

                    return this;
                }

                private async Task<ScenarioBuilder> AssertNewBlockAsync(UInt256 expectedBaseFee,
                    params Transaction[] transactions)
                {
                    await ExecuteAntecedentIfNeeded();
                    await _testRpcBlockchain.AddBlock(transactions);
                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block headBlock = blockTree.Head!;
                    Assert.That(headBlock.Header.BaseFeePerGas, Is.EqualTo(expectedBaseFee));

                    return this;
                }

                private async Task<ScenarioBuilder> AssertNewBlockWithDecreasedBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();

                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block startingBlock = blockTree.Head!;
                    await _testRpcBlockchain.AddBlock();
                    Block newBlock = blockTree.Head!;
                    Assert.Less(newBlock.Header.BaseFeePerGas, startingBlock.Header.BaseFeePerGas);

                    return this;
                }

                private async Task<ScenarioBuilder> AssertNewBlockWithIncreasedBaseFeeAsync()
                {
                    await ExecuteAntecedentIfNeeded();

                    IBlockTree blockTree = _testRpcBlockchain.BlockTree;
                    Block startingBlock = blockTree.Head!;
                    await _testRpcBlockchain.AddBlock();
                    Block newBlock = blockTree.Head!;
                    Assert.Less(startingBlock.Header.BaseFeePerGas, newBlock.Header.BaseFeePerGas);

                    return this;
                }

                private async Task ExecuteAntecedentIfNeeded()
                {
                    if (_antecedent is not null)
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
                return new();
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task BlockProducer_has_blocks_with_zero_base_fee_before_fork()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(5)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee();
            await scenario.Finish();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task BlockProducer_returns_correct_fork_base_fee()
        {
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(7)
                .CreateTestBlockchain()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee);
            await scenario.Finish();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task BaseFee_should_not_change_when_we_send_transactions_equal_gas_target()
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
                .AssertNewBlock(875000000)
                .AssertNewBlock(875000000)
                .AssertNewBlockWithDecreasedBaseFee();
            await scenario.Finish();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
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

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task When_base_fee_decreases_previously_fee_too_low_transaction_is_included()
        {
            long gasTarget = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .CreateTestBlockchain(gasTarget)
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .AssertNewBlock(Eip1559Constants.ForkBaseFee)
                .SendLegacyTransaction(gasTarget / 2, 7.GWei() / 10, nonce: UInt256.Zero)
                .AssertNewBlock(875000000)
                .AssertNewBlock(765625000)
                .AssertNewBlock(669921875) // added tx in 9th block
                .AssertNewBlock(628051758);
            await scenario.Finish();
        }
    }
}
