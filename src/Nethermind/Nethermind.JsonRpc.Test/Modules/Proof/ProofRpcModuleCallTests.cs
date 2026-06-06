// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Proof;

/// <summary>
/// Tests for <c>proof_call</c> — verifies the response shape (call output + execution witness),
/// header inclusion contract, and that the witness state is decodable into a usable stateless world.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class ProofRpcModuleCallTests
{
    private static readonly HeaderDecoder _headerDecoder = new();

    [Test]
    public async Task Proof_call_returns_witness_with_executed_block_header_as_first_entry()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await CreateTransferTx(blockchain);

        BlockHeader head = blockchain.BlockTree.Head!.Header;

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = TestItem.AddressB },
            new BlockParameter(head.Number));
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Witness.Headers, Is.Not.Empty);

        // The header at `blockParameter` is contractual: it must always be the first witness header.
        Rlp.ValueDecoderContext stream = new(result.Witness.Headers[0]);
        BlockHeader firstHeader = _headerDecoder.Decode(ref stream)!;
        Assert.That(firstHeader.Hash, Is.EqualTo(head.Hash!), "the executed-against block header must be included");
    }

    [TestCase("number")]
    [TestCase("hash")]
    [TestCase("latest")]
    public async Task Proof_call_accepts_block_number_hash_and_latest(string mode)
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await CreateTransferTx(blockchain);

        Block head = blockchain.BlockTree.Head!;
        BlockParameter param = mode switch
        {
            "number" => new BlockParameter(head.Number),
            "hash" => new BlockParameter(head.Hash!),
            "latest" => BlockParameter.Latest,
            _ => throw new ArgumentException(mode),
        };

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = TestItem.AddressB },
            param);
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Witness.State, Is.Not.Empty, "a call touching state must record state-trie nodes");
        Assert.That(result.Witness.Headers, Is.Not.Empty, "the executed-against header is always included");
    }

    [Test]
    public async Task Proof_call_against_genesis_is_rejected()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        using ResultWrapper<CallResultWithProof> result = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = TestItem.AddressA },
            new BlockParameter(0L));

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput), "genesis has no parent header for the witness walk");
        Assert.That(result.Result.Error, Does.Contain("genesis"), "the error message should explain the genesis rejection");
    }

    [Test]
    public async Task Proof_call_captures_account_and_one_storage_slot()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        // Contract that stores 0x42 at slot 0 (so the slot has a value to read) and returns SLOAD(0).
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0x42)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .Op(Instruction.SLOAD)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 200_000,
            },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // Return data: SLOAD(0) returned as 32 bytes big-endian — the trailing byte holds 0x42.
        Assert.That(result.Result, Is.Not.Null.And.Not.Empty);
        Assert.That(result.Result![^1], Is.EqualTo(0x42));
        Assert.That(result.Error, Is.Null, "clean success must not surface an in-payload error");

        Assert.That(result.Witness.Codes, Is.Not.Empty, "the called contract bytecode must be captured");
        Assert.That(result.Witness.State, Is.Not.Empty, "account + storage trie nodes must be captured");
        Assert.That(result.Witness.Keys, Is.Not.Empty, "accessed addresses and slots must be recorded");
    }

    [Test]
    public async Task Proof_call_with_blockhash_includes_referenced_block_header()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await CreateTransferTx(blockchain); // block 1 — referenced via BLOCKHASH below
        await CreateTransferTx(blockchain); // block 2 — advances chain so block 1 sits within the 256-block BLOCKHASH window

        // Contract that reads BLOCKHASH(1).
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.STOP)
            .Done;
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 200_000,
            },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // The collector walks back from the executed block to the lowest BLOCKHASH-touched block, so
        // the header set covers the path [BLOCKHASH-touched .. executed]. With BLOCKHASH(1), the path
        // includes the executed-against header plus at least block 1's header.
        Assert.That(result.Witness.Headers.Count, Is.GreaterThanOrEqualTo(2),
            "BLOCKHASH(1) must add at least one ancestor header beyond the executed-against header");

        BlockHeader? block1 = blockchain.BlockTree.FindHeader(1)!;
        bool foundBlock1 = false;
        foreach (byte[] headerRlp in result.Witness.Headers)
        {
            Rlp.ValueDecoderContext stream = new(headerRlp);
            if (_headerDecoder.Decode(ref stream)!.Hash == block1.Hash)
            {
                foundBlock1 = true;
                break;
            }
        }
        Assert.That(foundBlock1, Is.True, "the witness must include the header of the block referenced via BLOCKHASH");
    }

    [TestCaseSource(nameof(RevertCases))]
    public async Task Proof_call_revert_surfaces_error_and_witness(byte[] runtimeCode, string expectedMessage, string expectedDataPrefix)
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contractAddress, Gas = 200_000 },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // Revert surfaces no top-level Result; the payload lives in error.data (hex), mirroring eth_call.
        Assert.That(result.Result, Is.Null, "the success-path Result field is unused on revert");
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.ExecutionReverted));
        Assert.That(result.Error.Message, Is.EqualTo(expectedMessage));
        Assert.That(((string)result.Error.Data!), Does.StartWith(expectedDataPrefix));
        Assert.That(result.Witness.State, Is.Not.Empty);
        Assert.That(result.Witness.Codes, Is.Not.Empty);
    }

    // OOG must still return a witness — the deliberate divergence from eth_call, which would fail the RPC.
    [Test]
    public async Task Proof_call_returns_witness_and_error_on_out_of_gas()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        // Tight infinite loop — JUMPDEST then JUMP back to it. Burns gas until OOG.
        byte[] runtimeCode = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 50_000, // small budget so the infinite loop trips OOG quickly
            },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Error, Is.Not.Null, "OOG must surface an in-payload error so callers can act on it");
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.ExecutionError), "OOG is a non-revert in-VM error");
        Assert.That(result.Error.Message, Is.Not.Null.And.Not.Empty);
        Assert.That(result.Result, Is.Null, "OOG produces no return data");
        Assert.That(result.Witness.State, Is.Not.Empty, "the witness covering the OOG'd execution must be returned");
        Assert.That(result.Witness.Codes, Is.Not.Empty, "the executed contract bytecode must be captured");
    }

    /// <summary>
    /// Verifier check: the witness alone reconstructs the contract's account, bytecode, and storage —
    /// the building blocks a stateless light client needs to re-execute the call.
    /// </summary>
    [Test]
    public async Task Proof_call_witness_lets_a_verifier_reconstruct_state()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        // Contract: SSTORE(slot 0, 0xAB) in deploy init, then runtime returns SLOAD(0).
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.SLOAD)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
        byte[] initCode = Prepare.EvmCode
            .PushData(0xAB)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .ForInitOf(runtimeCode)
            .Done;

        UInt256 nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
        Transaction deployTx = Build.A.Transaction
            .WithNonce(nonce)
            .WithCode(initCode)
            .WithGasLimit(500_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(deployTx);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        BlockHeader sourceHeader = blockchain.BlockTree.FindHeader(blockNumber)!;

        using ResultWrapper<CallResultWithProof> proofWrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 200_000,
            },
            new BlockParameter(blockNumber));
        CallResultWithProof proof = proofWrapper.Data!;

        Assert.That(proof.Result, Is.Not.Null);
        Assert.That(proof.Result![^1], Is.EqualTo(0xAB));

        // The executed-against header must be present (used by the verifier to bind state root → block).
        Rlp.ValueDecoderContext firstHeaderStream = new(proof.Witness.Headers[0]);
        BlockHeader witnessHeader = _headerDecoder.Decode(ref firstHeaderStream)!;
        Assert.That(witnessHeader.Hash, Is.EqualTo(sourceHeader.Hash!));
        Assert.That(witnessHeader.StateRoot, Is.EqualTo(sourceHeader.StateRoot!));

        // Build a fresh world state from the witness contents and verify the contract is reachable.
        IWorldState statelessWorld = new WorldState(
            new TrieStoreScopeProvider(
                new RawTrieStore(proof.Witness.CreateNodeStorage()),
                proof.Witness.CreateCodeDb(),
                blockchain.LogManager),
            blockchain.LogManager);

        using IDisposable scope = statelessWorld.BeginScope(witnessHeader);

        Assert.That(statelessWorld.TryGetAccount(contractAddress, out AccountStruct account), Is.True,
            "the contract account must be reachable through witness-only state");
        byte[] reconstructedCode = statelessWorld.GetCode(contractAddress)!;
        Assert.That(reconstructedCode, Is.EqualTo(runtimeCode),
            "the contract bytecode must be reconstructible from witness.Codes");

        UInt256 slot0 = new(statelessWorld.Get(new StorageCell(contractAddress, 0)), isBigEndian: true);
        Assert.That(slot0, Is.EqualTo((UInt256)0xAB),
            "slot 0 must be reachable through witness state nodes");
    }

    /// <summary>
    /// Regression guard: a single-slot call must still capture the state-root node (via the
    /// <c>MultiAccountProofCollector</c> walk); without it, tiny-call witnesses fail stateless re-execution.
    /// </summary>
    [Test]
    public async Task Proof_call_single_slot_includes_state_root_in_witness()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0xCD).PushData(0).Op(Instruction.SSTORE)
            .PushData(0).Op(Instruction.SLOAD).PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).Op(Instruction.RETURN).Done;
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        BlockHeader head = blockchain.BlockTree.FindHeader(blockNumber)!;

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contractAddress, Gas = 200_000 },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Witness.State, Is.Not.Empty);

        // The state root must hash to one of the captured state-trie nodes — otherwise the verifier
        // has no anchor to begin reconstruction.
        bool foundRoot = false;
        foreach (byte[] rlp in result.Witness.State)
        {
            if (Keccak.Compute(rlp) == head.StateRoot)
            {
                foundRoot = true;
                break;
            }
        }
        Assert.That(foundRoot, Is.True, $"witness.State must include the state-root node ({head.StateRoot}) — without it a verifier cannot start trie traversal");
    }

    /// <summary>
    /// Cross-block correctness: a single pool entry serially reused across calls at different
    /// block heights must read the state at the correct historical state root each time.
    /// </summary>
    [Test]
    public async Task Proof_call_sequential_requests_at_different_blocks_see_correct_per_block_state()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        Address contract = await DeploySloadReturningContract(blockchain, 0xAA);
        long deployBlock = blockchain.BlockTree.Head!.Number;

        await CreateTransferTx(blockchain);
        await CreateTransferTx(blockchain);
        await CreateTransferTx(blockchain);
        long laterBlock = blockchain.BlockTree.Head!.Number;
        Assert.That(laterBlock, Is.GreaterThan(deployBlock));

        // Interleave calls between the two block heights several times.
        for (int round = 0; round < 8; round++)
        {
            using ResultWrapper<CallResultWithProof> atDeploy = blockchain.ProofRpcModule.proof_call(
                new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contract, Gas = 200_000 },
                new BlockParameter(deployBlock));
            using ResultWrapper<CallResultWithProof> atLater = blockchain.ProofRpcModule.proof_call(
                new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contract, Gas = 200_000 },
                new BlockParameter(laterBlock));

            Assert.That(atDeploy.Data.Result![^1], Is.EqualTo(0xAA), $"round {round}: deploy-block call");
            Assert.That(atLater.Data.Result![^1], Is.EqualTo(0xAA), $"round {round}: later-block call");

            // The deploy-block witness should reference the deploy block; the later-block witness
            // the later block. If a stale state root or scope leaked between rents, this would mismatch.
            Rlp.ValueDecoderContext deployStream = new(atDeploy.Data.Witness.Headers[0]);
            BlockHeader deployHdr = _headerDecoder.Decode(ref deployStream)!;
            Assert.That(deployHdr.Number, Is.EqualTo(deployBlock), $"round {round}: header at deploy-block call");

            Rlp.ValueDecoderContext laterStream = new(atLater.Data.Witness.Headers[0]);
            BlockHeader laterHdr = _headerDecoder.Decode(ref laterStream)!;
            Assert.That(laterHdr.Number, Is.EqualTo(laterBlock), $"round {round}: header at later-block call");
        }
    }

    private static async Task<Address> DeploySloadReturningContract(TestRpcBlockchain blockchain, byte markerValue)
    {
        // SSTORE(0, marker) on init; runtime returns SLOAD(0) padded to 32 bytes.
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0).Op(Instruction.SLOAD)
            .PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).Op(Instruction.RETURN).Done;
        byte[] initCode = Prepare.EvmCode
            .PushData(markerValue).PushData(0).Op(Instruction.SSTORE)
            .ForInitOf(runtimeCode).Done;

        UInt256 nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
        Transaction deployTx = Build.A.Transaction
            .WithNonce(nonce).WithCode(initCode).WithGasLimit(500_000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(deployTx);
        return contractAddress;
    }

    private static async Task CreateTransferTx(TestRpcBlockchain blockchain)
    {
        UInt256 nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction transferTx = Build.A.Transaction
            .WithNonce(nonce)
            .To(TestItem.AddressB)
            .WithValue(1)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(transferTx);
    }

    private static async Task<Address> DeployContract(TestRpcBlockchain blockchain, byte[] runtimeCode)
    {
        UInt256 nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);

        Transaction deployTx = Build.A.Transaction
            .WithNonce(nonce)
            .WithCode(initCode)
            .WithGasLimit(500_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await blockchain.AddBlock(deployTx);

        return contractAddress;
    }

    private static IEnumerable<TestCaseData> RevertCases()
    {
        // Unknown selector (0xdeadbeef) → BuildRevertError falls back to the bare "execution reverted".
        yield return new TestCaseData(
            Prepare.EvmCode
                .PushData(Bytes.FromHexString("deadbeef").PadRight(32))
                .PushData(0).Op(Instruction.MSTORE)
                .PushData(4).PushData(0).Op(Instruction.REVERT)
                .Done,
            "execution reverted",
            "0xdeadbeef")
            .SetName("Proof_call_revert_unknown_selector_falls_back_to_bare_sentinel");

        // ABI-encoded Error(string)("bad input") → decoded "execution reverted: bad input".
        // Payload: selector 08c379a0 | offset 0x20 | length 9 | "bad input" padded (4+32+32+32 = 100 bytes).
        byte[] selectorWord = Bytes.FromHexString("08c379a0").PadRight(32);
        byte[] dataWord = System.Text.Encoding.ASCII.GetBytes("bad input").PadRight(32);
        yield return new TestCaseData(
            Prepare.EvmCode
                .PushData(selectorWord).PushData(0).Op(Instruction.MSTORE)
                .PushData(0x20).PushData(4).Op(Instruction.MSTORE)
                .PushData(9).PushData(36).Op(Instruction.MSTORE)
                .PushData(dataWord).PushData(68).Op(Instruction.MSTORE)
                .PushData(100).PushData(0).Op(Instruction.REVERT)
                .Done,
            "execution reverted: bad input",
            "0x08c379a0")
            .SetName("Proof_call_revert_error_string_surfaces_decoded_reason");
    }
}
