// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
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
    public async Task Proof_call_returns_witness_with_executed_block_header_as_last_entry()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await CreateTransferTx(blockchain);

        BlockHeader head = blockchain.BlockTree.Head!.Header;

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = TestItem.AddressB },
            new BlockParameter(head.Number));
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Witness.Headers, Is.Not.Empty);

        // Contractual: executed-against header is the last entry (ascending block-number order).
        RlpReader reader = new(result.Witness.Headers[^1]);
        BlockHeader lastHeader = _headerDecoder.Decode(ref reader)!;
        Assert.That(lastHeader.Hash, Is.EqualTo(head.Hash!), "the executed-against block header must be included");
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

        BlockHeader block1 = blockchain.BlockTree.FindHeader(1)!;
        BlockHeader executed = blockchain.BlockTree.Head!.Header;

        // Contractual ordering: BLOCKHASH-touched ancestor first, executed-against header last.
        RlpReader firstReader = new(result.Witness.Headers[0]);
        BlockHeader firstHeader = _headerDecoder.Decode(ref firstReader)!;
        Assert.That(firstHeader.Hash, Is.EqualTo(block1.Hash),
            "the BLOCKHASH-touched ancestor must be the first witness header");

        RlpReader lastReader = new(result.Witness.Headers[^1]);
        BlockHeader lastHeader = _headerDecoder.Decode(ref lastReader)!;
        Assert.That(lastHeader.Hash, Is.EqualTo(executed.Hash!),
            "the executed-against header must remain the last witness header under BLOCKHASH");

        for (int i = 1; i < result.Witness.Headers.Count; i++)
        {
            RlpReader previousReader = new(result.Witness.Headers[i - 1]);
            RlpReader currentReader = new(result.Witness.Headers[i]);
            long prevNumber = _headerDecoder.Decode(ref previousReader)!.Number;
            long curNumber = _headerDecoder.Decode(ref currentReader)!.Number;
            Assert.That(curNumber, Is.GreaterThan(prevNumber),
                $"witness header at index {i} must have a higher block number than its predecessor");
        }
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
    /// <summary>
    /// Regression guard for the deleted <c>Debug_witness_includes_trie_nodes_for_storage_set_without_prior_read_then_reverted</c>:
    /// when a slot is written (via SSTORE → WorldState.Set) and then reverted (via REVERT → WorldState.Restore),
    /// the cached write is discarded and the trie is never traversed during the call. The witness must
    /// still include the storage trie nodes for the slot — <see cref="WitnessGeneratingWorldState.GetWitness"/>
    /// re-walks touched keys via <c>MultiAccountProofCollector</c> + per-account <c>AccountProofCollector</c> to
    /// capture them. A cross-client (geth) verifier cannot reconstruct the slot without these nodes.
    /// </summary>
    [Test]
    public async Task Proof_call_includes_trie_nodes_for_storage_sstore_then_reverted()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        // Runtime: SSTORE(0, 0xEE) then REVERT with empty data. The slot is written then reverted
        // in the same call — the trie is never traversed during the call. The only way the witness
        // covers slot 0's storage trie is via the post-execution re-walk of touched keys in
        // WitnessGeneratingWorldState.GetWitness.
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0xEE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;
        // Init: SSTORE(0, 0xEE) so slot 0 is committed to the trie at deploy time. This gives the
        // parent state a real storage trie node for slot 0 that the post-execution re-walk must
        // find and re-capture; without it, the parent state has no slot 0 storage node and the
        // "expected storage proof" baseline would be empty.
        byte[] initCode = Prepare.EvmCode
            .PushData(0xEE)
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

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 200_000,
            },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // REVERT surfaces an in-payload error but the witness is still returned.
        Assert.That(result.Result, Is.Null, "REVERT must null out the success-path Result");
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.ExecutionReverted));
        Assert.That(result.Witness.State, Is.Not.Empty,
            "the witness must include storage trie nodes for the SSTORE'd-then-reverted slot");

        // Compute the expected storage proof by walking the parent state's storage trie directly.
        AccountProofCollector expectedCollector = new(contractAddress, [UInt256.Zero]);
        blockchain.StateReader.RunTreeVisitor(expectedCollector, sourceHeader);
        AccountProof expectedProof = expectedCollector.BuildResult();
        byte[][] expectedStorageProofNodes = expectedProof.StorageProofs!
            .SelectMany(sp => sp.Proof!)
            .ToArray();
        Assert.That(expectedStorageProofNodes, Is.Not.Empty,
            "the contract should have a non-empty storage proof for slot 0 in the parent state");

        // The witness must contain every expected storage trie node by hash. If the
        // MultiAccountProofCollector / per-account AccountProofCollector re-walk were dropped, this
        // would fail because the SSTORE was reverted (the trie was never traversed during the call)
        // and only the re-walk could have captured these nodes.
        HashSet<Hash256> witnessNodeHashes = result.Witness.State
            .Select(Keccak.Compute)
            .ToHashSet();
        foreach (byte[] expectedNode in expectedStorageProofNodes)
        {
            Assert.That(witnessNodeHashes, Does.Contain(Keccak.Compute(expectedNode)),
                "witness must include the storage trie node even though the slot was SSTORE'd then reverted");
        }
    }

    /// <summary>
    /// Regression guard for the deleted <c>Debug_executionWitnessCall_without_gas_field_still_records_full_witness</c>:
    /// the <c>gas</c> field is documented as optional in the call request, and callers reasonably assume
    /// the witness is recorded the same whether or not they pass gas explicitly. If that symmetry breaks
    /// again, a caller who omits gas gets a near-empty witness that silently succeeds but fails
    /// stateless re-execution downstream. Seen in the wild with surge-raiko's L1STATICCALL preflight.
    /// </summary>
    [Test]
    public async Task Proof_call_with_and_without_gas_field_produces_same_witness_shape()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await CreateTransferTx(blockchain);
        Address contractAddress = await DeploySloadReturningContract(blockchain, 0x55);

        long blockNumber = blockchain.BlockTree.Head!.Number;

        // With gas explicitly passed — the control case.
        using ResultWrapper<CallResultWithProof> withGas = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
                Gas = 200_000,
            },
            new BlockParameter(blockNumber));

        // Without gas — what most call sites end up sending. `{to}` is the natural shape for a view
        // call, and callers don't want to have to know the block's gas limit.
        using ResultWrapper<CallResultWithProof> withoutGas = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                To = contractAddress,
            },
            new BlockParameter(blockNumber));

        Assert.That(withGas.Data, Is.Not.Null);
        Assert.That(withoutGas.Data, Is.Not.Null);
        Assert.That(withoutGas.Data!.Error, Is.Null,
            "omitting gas must not be treated as an error");
        Assert.That(withoutGas.Data.Result, Is.Not.Null.And.Not.Empty,
            "omitting gas must still execute the call to completion");

        // The two paths must produce witnesses of the same shape.
        Assert.That(withoutGas.Data.Witness.State, Is.Not.Empty,
            "omitting gas must not empty the state node set");
        Assert.That(withoutGas.Data.Witness.Codes, Is.Not.Empty,
            "omitting gas must still capture called-contract bytecode");
        Assert.That(withoutGas.Data.Witness.State.Count, Is.EqualTo(withGas.Data.Witness.State.Count),
            "state-node count should match between with-gas and without-gas calls");
        Assert.That(withoutGas.Data.Witness.Codes.Count, Is.EqualTo(withGas.Data.Witness.Codes.Count),
            "code count should match between with-gas and without-gas calls");
        Assert.That(withoutGas.Data.Witness.Keys.Count, Is.EqualTo(withGas.Data.Witness.Keys.Count),
            "key count should match between with-gas and without-gas calls");
    }

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
        RlpReader lastHeaderReader = new(proof.Witness.Headers[^1]);
        BlockHeader witnessHeader = _headerDecoder.Decode(ref lastHeaderReader)!;
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
    /// Regression: a legacy call with no GasPrice on a post-London chain must zero BaseFeePerGas
    /// (matching <c>eth_call</c>); without it, BuyGas rejects with MaxFeePerGasBelowBaseFee.
    /// </summary>
    [Test]
    public async Task Proof_call_legacy_tx_with_no_gas_price_on_post_london_chain_zeroes_base_fee()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain
            .ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(London.Instance));

        await CreateTransferTx(blockchain);
        BlockHeader head = blockchain.BlockTree.Head!.Header;

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = TestItem.AddressB },
            new BlockParameter(head.Number));

        Assert.That(wrapper.Data, Is.Not.Null);
        Assert.That(wrapper.Data!.Error, Is.Null,
            "legacy call with no GasPrice must zero BaseFeePerGas (mirroring eth_call), not surface MaxFeePerGasBelowBaseFee");
        Assert.That(wrapper.Data.Witness.State, Is.Not.Empty);
    }

    /// <summary>
    /// Regression: a call touching two accounts must capture the storage trie for both. The pre-fix
    /// <c>MultiAccountProofCollector</c> keyed its storage-walk discriminator by an address hash
    /// the visitor never provided, so the second account's storage was silently dropped.
    /// </summary>
    [Test]
    public async Task Proof_call_with_two_accounts_captures_storage_trie_for_each()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Address contract = await DeploySloadReturningContract(blockchain, 0x77);
        long blockNumber = blockchain.BlockTree.Head!.Number;

        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contract, Gas = 200_000 },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // Touched accounts: sender (AddressA) and the contract. The witness must record both
        // accounts and at least one slot key.
        Assert.That(result.Witness.Keys.Count, Is.GreaterThanOrEqualTo(3),
            "witness should record the sender, the contract, and at least one slot key");
        Assert.That(result.Witness.State, Is.Not.Empty);
    }

    /// <summary>
    /// Round-trip guard for opcodes that touch a second account's state without calling into it.
    /// The pre-existing test exercises SLOAD; BALANCE is the cheapest second case — it forces the
    /// witness to capture the target's account leaf, and the round-trip check proves the state-trie
    /// path is reconstructable without the chain.
    /// </summary>
    [Test]
    public async Task Proof_call_witness_round_trips_through_stateless_reconstruction_for_balance_opcode()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        Address target = await DeploySloadReturningContract(blockchain, 0x33);
        byte[] callerCode = Prepare.EvmCode
            .PushData(target)
            .Op(Instruction.BALANCE)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        Address caller = await DeployContract(blockchain, callerCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = caller, Gas = 200_000 },
            new BlockParameter(blockNumber));
        CallResultWithProof proof = wrapper.Data!;

        Assert.That(proof.Witness.State, Is.Not.Empty);

        RlpReader reader = new(proof.Witness.Headers[^1]);
        BlockHeader witnessHeader = _headerDecoder.Decode(ref reader)!;
        IWorldState statelessWorld = new WorldState(
            new TrieStoreScopeProvider(
                new RawTrieStore(proof.Witness.CreateNodeStorage()),
                proof.Witness.CreateCodeDb(),
                blockchain.LogManager),
            blockchain.LogManager);
        using IDisposable scope = statelessWorld.BeginScope(witnessHeader);

        Assert.That(statelessWorld.TryGetAccount(target, out _), Is.True,
            "BALANCE on a target must capture the target's account leaf in the witness");
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

            // Each witness must reference its own block (a stale state root or scope leak would mismatch).
            RlpReader deployReader = new(atDeploy.Data.Witness.Headers[^1]);
            BlockHeader deployHdr = _headerDecoder.Decode(ref deployReader)!;
            Assert.That(deployHdr.Number, Is.EqualTo(deployBlock), $"round {round}: header at deploy-block call");

            RlpReader laterReader = new(atLater.Data.Witness.Headers[^1]);
            BlockHeader laterHdr = _headerDecoder.Decode(ref laterReader)!;
            Assert.That(laterHdr.Number, Is.EqualTo(laterBlock), $"round {round}: header at later-block call");
        }
    }

    /// <summary>
    /// Pool isolation under concurrent load: many proof_call requests fired in parallel against
    /// the same factory must each return a correct, non-shared witness. Exercises the
    /// <c>Interlocked.Exchange</c> on <c>RentedScope._disposed</c> and the soft-cap counter
    /// in <see cref="WitnessGeneratingBlockProcessingEnvFactory"/> — a regression that races two
    /// concurrent renters on the same pooled entry would surface here as a torn witness (wrong
    /// state root, missing trie nodes, or wrong executed-against header).
    /// </summary>
    [Test]
    public async Task Proof_call_concurrent_requests_get_isolated_witnesses()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Address contract = await DeploySloadReturningContract(blockchain, 0xBB);
        long deployBlock = blockchain.BlockTree.Head!.Number;

        await CreateTransferTx(blockchain);
        await CreateTransferTx(blockchain);
        long laterBlock = blockchain.BlockTree.Head!.Number;

        // Interleave same-block and cross-block requests; a torn-witness regression would surface
        // as either an exception in one of the tasks or an assertion failure below.
        int requestCount = Environment.ProcessorCount * 2;
        Task<(long block, byte result)>[] tasks = new Task<(long, byte)>[requestCount];
        for (int i = 0; i < requestCount; i++)
        {
            long block = (i & 1) == 0 ? deployBlock : laterBlock;
            tasks[i] = Task.Run(async () =>
            {
                using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
                    new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contract, Gas = 200_000 },
                    new BlockParameter(block));
                CallResultWithProof result = wrapper.Data!;
                Assert.That(result.Result, Is.Not.Null.And.Not.Empty, $"task {i}: result");
                Assert.That(result.Result![^1], Is.EqualTo(0xBB), $"task {i}: storage value");

                // Header must match the requested block — a scope leak between two concurrent
                // rents would manifest as a mismatched block number here.
                RlpReader reader = new(result.Witness.Headers[^1]);
                BlockHeader hdr = _headerDecoder.Decode(ref reader)!;
                Assert.That(hdr.Number, Is.EqualTo(block), $"task {i}: header number");
                return (block, result.Result[^1]);
            });
        }

        (long, byte)[] all = await Task.WhenAll(tasks);
        Assert.That(all.Length, Is.EqualTo(requestCount));
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

    /// <summary>
    /// Copilot review fix: <c>proof_call</c> must include <c>error.data = "0x"</c> on REVERT with an
    /// empty payload, matching <c>eth_call</c>. Consumers branch on the presence of <c>data</c>;
    /// silently dropping it for empty payloads would diverge from <c>eth_call</c>'s wire format.
    /// </summary>
    [Test]
    public async Task Proof_call_revert_with_empty_payload_includes_data_0x()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        // REVERT(0, 0) — empty payload (length = 0, offset = 0).
        byte[] runtimeCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;
        Address contractAddress = await DeployContract(blockchain, runtimeCode);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc { To = contractAddress, Gas = 200_000 },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.ExecutionReverted));
        Assert.That(result.Error.Data, Is.Not.Null, "empty REVERT payload must still set data — matches eth_call");
        Assert.That((string)result.Error.Data!, Is.EqualTo("0x"));
    }

    /// <summary>
    /// Copilot review fix: a <c>proof_call</c> request that includes <c>from</c> but omits
    /// <c>nonce</c> must succeed against the current state nonce, matching <c>eth_call</c>'s
    /// "ignore caller-supplied nonce" behavior. Without this fix, the EVM's pre-VM validation
    /// fails with TransactionNonceTooHigh/Low before the call runs, and no witness is returned.
    /// </summary>
    [Test]
    public async Task Proof_call_with_from_but_no_nonce_resolves_nonce_from_state()
    {
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Address contract = await DeploySloadReturningContract(blockchain, 0x42);

        long blockNumber = blockchain.BlockTree.Head!.Number;
        using ResultWrapper<CallResultWithProof> wrapper = blockchain.ProofRpcModule.proof_call(
            new Facade.Eth.RpcTransaction.LegacyTransactionForRpc
            {
                From = TestItem.AddressA, // current state nonce is 0 in the test chain
                To = contract,
                Gas = 200_000,
                // No Nonce set — the collector must resolve it from state.
            },
            new BlockParameter(blockNumber));
        CallResultWithProof result = wrapper.Data!;

        // The SLOAD-returning contract returns 0x42 in the last byte of its 32-byte return.
        // If the nonce fix regressed, the EVM would reject the call pre-VM with
        // TransactionNonceTooHigh (or similar) and wrapper.Data would be null.
        Assert.That(result.Result, Is.Not.Null.And.Not.Empty,
            "from-but-no-nonce must not be rejected pre-VM");
        Assert.That(result.Result![^1], Is.EqualTo(0x42));
    }
}
