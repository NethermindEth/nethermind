// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using Nethermind.StatelessInputGen;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Consensus.Test.Stateless;

public class ExistingBlockWitnessTests
{
    // Increments the slot named by the first calldata word, then zeroes the slot named by the second.
    // PUSH1 0 CALLDATALOAD DUP1 SLOAD PUSH1 1 ADD SWAP1 SSTORE PUSH1 0 PUSH1 32 CALLDATALOAD SSTORE STOP
    private static readonly byte[] IncrementAndClear = Bytes.FromHexString("6000358054600101905560006020355500");

    [Test]
    [NonParallelizable]
    public async Task Stateless_executor_collects_recovery_inputs_from_actual_execution()
    {
        ValueHash256 message = ValueKeccak.Compute("stateless recovery"u8);
        Signature signature = new EthereumEcdsa(BlockchainIds.Mainnet).Sign(TestItem.PrivateKeyA, in message);
        byte[] call = new byte[128];
        message.Bytes.CopyTo(call);
        call[63] = (byte)(27 + signature.RecoveryId);
        signature.Bytes.CopyTo(call.AsSpan(64));
        Transaction transaction = Build.A.Transaction.WithTo(ECRecoverPrecompile.Address).WithData(call)
            .WithValue(0).WithGasLimit(100000).WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        (Block block, Witness witness, ISpecProvider specProvider) = StatelessInputGeneratorTests.CreateBlock(
            amsterdam: true, currentChainActivation: true, requests: false, transaction: transaction);
        using (witness)
        {
            byte[] stateless = (await InputGenerator.EncodeInput(block, witness, specProvider))!;
            byte[] trusted = TrustedInput.FromStatelessInput(stateless);
            StatelessValidationResult.Decode(StatelessExecutor.ExecuteTrusted(trusted), out StatelessValidationResult result);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.IsSuccess, Is.True, "The input must execute before recovery calls can be collected.");
                Assert.That(StatelessExecutor.CollectRecoveryInputs(trusted), Is.EqualTo(new[] { call }));
            }
        }
    }

    private const int Contracts = 24;
    private const int SlotsPerContract = 64;

    [Test]
    public void Stateless_environment_replays_valid_blocks_with_optional_tracing([Values] bool traced)
    {
        (Block block, Witness witness, ISpecProvider specProvider) = StatelessInputGeneratorTests.CreateBlock(
            amsterdam: false, currentChainActivation: true);
        using (witness)
        {
            StatelessBlockProcessingEnv stateless = new(witness, specProvider, Always.Valid, LimboLogs.Instance);
            IBlockTracer tracer = Substitute.For<IBlockTracer>();
            tracer.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
            StatelessBlockProcessingResult result = traced ? stateless.Process(block, true, tracer) : stateless.Process(block);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.IsValid, Is.True, result.Error);
                Assert.That(result.ProcessedBlock?.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
            }
            if (traced)
            {
                tracer.Received(1).StartNewBlockTrace(Arg.Any<Block>());
                tracer.Received(1).StartNewTxTrace(Arg.Any<Transaction>());
                tracer.Received(1).EndBlockTrace();
            }
        }
    }

    [Test]
    public async Task Witness_of_a_block_touching_many_storage_tries_replays_statelessly()
    {
        Address[] contracts = [.. Enumerable.Range(0, Contracts).Select(static i => new Address(Keccak.Compute($"contract{i}").Bytes[..Address.Size]))];
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance))
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                foreach (Address contract in contracts)
                {
                    state.CreateAccount(contract, UInt256.One);
                    state.InsertCode(contract, IncrementAndClear, specProvider.GenesisSpec);
                    for (int slot = 1; slot <= SlotsPerContract; slot++)
                        state.Set(new StorageCell(contract, (UInt256)slot), (UInt256)slot);
                }
            }));

        // Every contract gets an update and a deletion, so each storage trie needs both its paths and any collapse sibling.
        Transaction[] txs = [.. contracts.Select((contract, i) => Build.A.Transaction
            .WithNonce((ulong)i)
            .WithTo(contract)
            .WithData([.. ((UInt256)(i % SlotsPerContract + 1)).ToBigEndian(), .. ((UInt256)((i + 7) % SlotsPerContract + 1)).ToBigEndian()])
            .WithGasLimit(100_000)
            .WithGasPrice(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject)];
        Block block = await chain.AddBlock(txs);
        Assert.That(block.Transactions, Has.Length.EqualTo(Contracts), "precondition: every call landed in one block");

        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        using IWitnessGeneratingBlockProcessingEnvScope scope = chain.Container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>().CreateScope();
        using Witness witness = scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block);

        StatelessBlockProcessingEnv stateless = new(witness, chain.SpecProvider, Always.Valid, LimboLogs.Instance);
        using IDisposable stateScope = stateless.WorldState.BeginScope(parent);
        (Block processed, _) = stateless.BlockProcessor.ProcessOne(
            block.WithReplacedHeader(block.Header.Clone()), ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None);

        Assert.That(processed.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
    }
}
