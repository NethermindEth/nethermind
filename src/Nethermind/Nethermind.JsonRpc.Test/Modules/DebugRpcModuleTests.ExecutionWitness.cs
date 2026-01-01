// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
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

        Witness witness = response.Should().BeOfType<JsonRpcSuccessResponse>()
            .Which.Result.Should().BeOfType<Witness>()
            .Subject;

        witness.Headers.Should().NotBeEmpty();
        witness.State.Should().NotBeEmpty();

        CheckStatelessProcessing(blockchain, witness, block);
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
