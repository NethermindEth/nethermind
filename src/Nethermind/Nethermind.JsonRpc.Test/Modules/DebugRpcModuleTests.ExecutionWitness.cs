// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    [TestCaseSource(nameof(ExecutionWitnessSource))]
    public async Task Debug_executionWitness_can_be_used_for_stateless_execution_for_multiple_blocks(long blockNumber)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain blockchain = ctx.Blockchain;

        // Create a few blocks for stateless reprocessing
        // Especially, blocks that touch sensitive opcodes relevant to stateless processing
        Block transferTxBlock = await CreateTransferTx(blockchain);
        Address contractAddress = await CreateDeployTx(blockchain, transferTxBlock.Number);
        await CreateContractCallTx(blockchain, contractAddress);

        Block? block = blockchain.BlockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        block.Should().NotBeNull();
        block!.Hash.Should().NotBeNull();

        JsonRpcResponse response = await RpcTest.TestRequest(ctx.DebugRpcModule, "debug_executionWitness", blockNumber);

        // Cannot generate witness for genesis block as the block itself does not contain any transaction
        // responsible for the state setup.
        if (blockNumber == 0)
        {
            response.Should().BeOfType<JsonRpcErrorResponse>().Which.Error!.Message.Should().Be("Cannot generate witness for genesis block");
            return;
        }

        using Witness witness = response.Should().BeOfType<JsonRpcSuccessResponse>()
            .Which.Result.Should().BeOfType<Witness>()
            .Subject;

        witness.Headers.Should().NotBeEmpty();
        witness.State.Should().NotBeEmpty();

        CheckStatelessProcessing(blockchain, witness, block);
    }

    /// <summary>
    /// Tests that when a storage slot is written to (via WorldState.Set, not through SSTORE opcode
    /// which reads the slot beforehand via GetOriginal for gas computation) without being read first,
    /// and the write is then reverted, the associated trie nodes to the slot are still captured in the
    /// witness (thanks to AccountProofCollector tree visitor in GetWitness).
    ///
    /// This is important because Set() does not traverse the trie — it only caches the write.
    /// If the write is reverted, the cached value is discarded and the trie was never traversed.
    /// Without the tree visitor pattern in GetWitness, these trie nodes would be missing from the
    /// witness. Other clients like geth reads state directly when a write occurs. In order to be compatible
    /// with them so that our generated witness can be used to perform their stateless execution,
    /// we record those trie nodes.
    /// </summary>
    [Test]
    public async Task Debug_witness_includes_trie_nodes_for_storage_set_without_prior_read_then_reverted()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain blockchain = ctx.Blockchain;

        // Deploy a contract that stores a non-zero value in slot 0 when called.
        // The purpose is to create a trie node unique to that slot that can only be
        // captured if the trie is traversed until that slot, not some intermediate node
        // that could be captured due to other slots or accounts.
        UInt256 storageSlot = 0;
        Address contractAddress = await DeployAndCallContractWithStorage(blockchain, storageSlot);

        BlockHeader parent = blockchain.BlockTree.Head!.Header;

        // Construct witness-generating infrastructure manually to demonstrate the tree visitor pattern.
        IReadOnlyTrieStore readOnlyTrieStore = blockchain.Container.Resolve<IReadOnlyTrieStore>();
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(blockchain.DbProvider, true);
        WitnessCapturingTrieStore capturingTrieStore = new(readOnlyDbProvider.StateDb, readOnlyTrieStore);
        StateReader stateReader = new(capturingTrieStore, readOnlyDbProvider.CodeDb, blockchain.LogManager);
        WorldState worldState = new(new TrieStoreScopeProvider(capturingTrieStore, readOnlyDbProvider.CodeDb, blockchain.LogManager), blockchain.LogManager);
        WitnessGeneratingHeaderFinder headerFinder = new(blockchain.Container.Resolve<IHeaderFinder>());
        WitnessGeneratingWorldState witnessState = new(worldState, stateReader, capturingTrieStore, headerFinder);

        using (witnessState.BeginScope(parent))
        {
            int capturedNodesBefore = capturingTrieStore.TouchedNodesRlp.Count();

            // Take snapshot before modifying state
            Snapshot snapshot = witnessState.TakeSnapshot();

            // Write to the contract's storage slot WITHOUT reading it first.
            // This simulates a direct state modification (not through SSTORE opcode which reads
            // the slot beforehand via GetOriginal for gas computation).
            // Set() records the slot in _storageSlots but does NOT cause trie traversal.
            StorageCell storageCell = new(contractAddress, storageSlot);
            witnessState.Set(in storageCell, new StorageValue([99])); // some random value (does not matter)

            // Verify no new trie nodes were captured by the trie store during Set()
            capturingTrieStore.TouchedNodesRlp.Count().Should().Be(capturedNodesBefore,
                "Set() should not traverse the trie");

            // Simulate tx revert by reverting the write — cached write is discarded but _storageSlots retains the slot
            witnessState.Restore(snapshot);

            // GetWitness runs AccountProofCollector tree visitor for all recorded slots,
            // which traverses the trie and captures proof nodes even for not-read-and-set-but-reverted slots
            using Witness witness = witnessState.GetWitness(parent);

            // Collect the expected storage proof from the parent state
            AccountProofCollector collector = new(contractAddress, [storageSlot]);
            blockchain.StateReader.RunTreeVisitor(collector, parent);
            AccountProof accountProof = collector.BuildResult();

            byte[][] storageProofNodes = accountProof.StorageProofs!
                .SelectMany(sp => sp.Proof!)
                .ToArray();

            storageProofNodes.Should().NotBeEmpty(
                "the contract should have a non-empty storage proof for slot 0 in the parent state");

            HashSet<Hash256> witnessNodeHashes = witness.State
                .Select(Keccak.Compute)
                .ToHashSet();

            foreach (byte[] proofNode in storageProofNodes)
            {
                witnessNodeHashes.Should().Contain(Keccak.Compute(proofNode),
                    "witness should contain storage trie proof node even though the slot was " +
                    "only written to (not read) and then reverted");
            }
        }
    }

    /// <summary>
    /// Deploys a contract whose runtime code stores value 1 at the given slot,
    /// then calls it so the storage is committed to the trie.
    /// </summary>
    private static async Task<Address> DeployAndCallContractWithStorage(TestRpcBlockchain blockchain, UInt256 storageSlot)
    {
        UInt256 deployNonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        byte[] runtimeCode = Prepare.EvmCode
            .PushData(1)             // value
            .PushData(storageSlot)   // key (slot)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, deployNonce);

        Transaction deployTx = Build.A.Transaction
            .WithNonce(deployNonce)
            .WithCode(initCode)
            .WithGasLimit(200_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(deployTx);

        // Call the contract to execute the SSTORE and commit storage to the trie
        UInt256 callNonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction callTx = Build.A.Transaction
            .WithNonce(callNonce)
            .To(contractAddress)
            .WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(callTx);

        return contractAddress;
    }

    private static IEnumerable<TestCaseData> ExecutionWitnessSource()
    {
        // 7 blocks in the test where this test case source is used
        for (long blockNumber = 0; blockNumber < 7; blockNumber++)
            yield return new TestCaseData(blockNumber);
    }

    private static async Task<Block> CreateTransferTx(TestRpcBlockchain blockchain)
    {
        UInt256 transferNonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction transferTx = Build.A.Transaction
            .WithNonce(transferNonce)
            .To(TestItem.AddressB)
            .WithValue(1)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return await blockchain.AddBlock(transferTx);
    }

    private static async Task<Address> CreateDeployTx(TestRpcBlockchain blockchain, long blockWhoseHashToGet)
    {
        UInt256 deployNonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            // BLOCKHASH opcode forces getting block headers from blockTree later stored in witness
            .PushData(blockWhoseHashToGet) // block created above
            .Op(Instruction.BLOCKHASH)
            .PushData(blockWhoseHashToGet - 1) // block created from chain setup (see AddBlockOnStart)
            .Op(Instruction.BLOCKHASH)
            .PushData(10) // block does not exist in chain, should return a zero hash and therefore not add any block header in witness
            .Op(Instruction.BLOCKHASH)
            // TestItem.AddressA contains "0xabcd" from the chain setup, the EXTCODECOPY opcode forces getting bytecode from codeInfoRepository
            .EXTCODECOPY(TestItem.AddressA, 0, 0, 2)
            .Op(Instruction.STOP)
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, deployNonce);

        Transaction deployTx = Build.A.Transaction
            .WithNonce(deployNonce)
            .WithCode(initCode)
            .WithGasLimit(200_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(deployTx);

        return contractAddress;
    }

    private static async Task CreateContractCallTx(TestRpcBlockchain blockchain, Address contractAddress)
    {
        UInt256 callNonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction callTx = Build.A.Transaction
            .WithNonce(callNonce)
            .To(contractAddress)
            .WithGasLimit(200_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        await blockchain.AddBlock(callTx);
    }

    private static void CheckStatelessProcessing(TestRpcBlockchain blockchain, Witness witness, Block expectedBlock)
    {
        BlockHeader? parent = blockchain.BlockTree.FindHeader(expectedBlock.Header.ParentHash!, BlockTreeLookupOptions.RequireCanonical);
        parent.Should().NotBeNull();

        StatelessBlockProcessingEnv statelessEnv = new(
            witness,
            blockchain.SpecProvider,
            Always.Valid,
            blockchain.LogManager);

        using var scope = statelessEnv.WorldState.BeginScope(parent!);
        (Block processed, _) = statelessEnv.BlockProcessor.ProcessOne(
            expectedBlock,
            ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance,
            blockchain.SpecProvider.GetSpec(expectedBlock.Header));

        processed.Hash.Should().Be(expectedBlock.Hash!);
    }
}
