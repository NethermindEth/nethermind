// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezBlockValidatorTests
{
    private readonly EezBlockValidator _validator = new(Always.Valid, Always.Valid, Always.Valid, new TestSpecProvider(Osaka.Instance), LimboLogs.Instance);

    [Test]
    public void ValidateOrphanedBlock_WithBlobTransaction_IsInvalid()
    {
        Transaction blobTx = Build.A.Transaction.WithShardBlobTxTypeAndFields().SignedAndResolved().TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(blobTx).WithWithdrawals(0).TestObject;

        bool isValid = _validator.ValidateOrphanedBlock(block, out string? error);

        Assert.That(isValid, Is.False, "EEZ L2 blocks carry no blob transactions");
        Assert.That(error, Is.EqualTo(EezBlockValidator.BlobTransactionNotAllowed), "the blob rule, not another check, rejects the block");
    }

    [Test]
    public void ValidateOrphanedBlock_WithFrameTransactionCarryingBlobs_IsInvalid()
    {
        Transaction frameTx = new() { Type = TxType.FrameTx, BlobVersionedHashes = [new byte[32]] };
        Block block = Build.A.Block.WithNumber(1).WithTransactions(frameTx).WithWithdrawals(0).TestObject;

        bool isValid = _validator.ValidateOrphanedBlock(block, out string? error);

        Assert.That(isValid, Is.False, "blobs are rejected whatever transaction type carries them");
        Assert.That(error, Is.EqualTo(EezBlockValidator.BlobTransactionNotAllowed), "the blob rule, not another check, rejects the block");
    }

    [Test]
    public void ValidateOrphanedBlock_WithWithdrawals_IsInvalid()
    {
        Block block = Build.A.Block.WithNumber(1).WithBlobGasUsed(0).WithExcessBlobGas(0).WithWithdrawals(1).TestObject;

        bool isValid = _validator.ValidateOrphanedBlock(block, out string? error);

        Assert.That(isValid, Is.False, "beacon withdrawals would mint L2 ether outside system transactions");
        Assert.That(error, Is.EqualTo(EezBlockValidator.WithdrawalsNotAllowed), "the withdrawal rule, not another check, rejects the block");
    }

    [Test]
    public void ValidateBodyAgainstHeader_WithWithdrawals_IsInvalid()
    {
        Block block = Build.A.Block.WithNumber(1).WithBlobGasUsed(0).WithExcessBlobGas(0).WithWithdrawals(1).TestObject;

        bool isValid = _validator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error);

        Assert.That(isValid, Is.False, "bodies fetched for a known header follow the same withdrawal rule");
        Assert.That(error, Is.EqualTo(EezBlockValidator.WithdrawalsNotAllowed), "the withdrawal rule, not another check, rejects the body");
    }

    [Test]
    public void ValidateOrphanedBlock_WithEmptyWithdrawalsAndNoBlobs_IsValid()
    {
        Block block = Build.A.Block.WithNumber(1).WithBlobGasUsed(0).WithExcessBlobGas(0).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).WithWithdrawals(0).TestObject;

        bool isValid = _validator.ValidateOrphanedBlock(block, out string? error);

        Assert.That(isValid, Is.True, $"an EEZ block with an empty withdrawal list is valid, got: {error}");
    }

    [TestCase("beacon")]
    [TestCase("requests")]
    [TestCase("difficulty")]
    [TestCase("nonce")]
    [TestCase("ommers")]
    public void Validate_L2OnlyProfile_RejectsConsensusClaims(string change)
    {
        EezBlockValidator validator = new(Always.Valid, Always.Valid, Always.Valid, new TestSpecProvider(Osaka.Instance), LimboLogs.Instance,
            EezChainRules.Rollup0.WithoutConsensusLayerCalls());
        Block block = Build.A.Block.WithNumber(1).WithBlobGasUsed(0).WithExcessBlobGas(0).WithWithdrawals(0).TestObject;
        block.Header.Difficulty = 0;
        block.Header.Nonce = 0;
        block.Header.ParentBeaconBlockRoot = Keccak.Zero;
        block.Header.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
        Assert.That(validator.ValidateOrphanedBlock(block, out _), Is.True, "precondition: the unmodified L2 profile is accepted");
        switch (change)
        {
            case "beacon": block.Header.ParentBeaconBlockRoot = Keccak.Compute("nonzero beacon root"); break;
            case "requests": block.Header.RequestsHash = Keccak.Compute("nonempty requests"); break;
            case "difficulty": block.Header.Difficulty = 1; break;
            case "nonce": block.Header.Nonce = 1; break;
            case "ommers": block.Header.UnclesHash = Keccak.Zero; break;
        }
        Assert.That(validator.ValidateOrphanedBlock(block, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(EezBlockValidator.ConsensusLayerFieldsNotAllowed));
        Assert.That(validator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? bodyError), Is.False);
        Assert.That(bodyError, Is.EqualTo(error));
    }

    [TestCase("beacon")]
    [TestCase("requests")]
    public void StatelessEnvironment_RejectsConsensusClaims(string change)
    {
        EezStatelessExecutor executor = new(new TestSpecProvider(Osaka.Instance), EezChainRules.Rollup0.WithoutConsensusLayerCalls(), LimboLogs.Instance);
        using Witness witness = new() { Codes = new ArrayPoolList<byte[]>(0), State = new ArrayPoolList<byte[]>(0), Keys = new ArrayPoolList<byte[]>(0), Headers = new ArrayPoolList<byte[]>(0) };
        StatelessBlockProcessingEnv environment = executor.CreateEnvironment(witness);
        Block block = Build.A.Block.WithNumber(1).WithBlobGasUsed(0).WithExcessBlobGas(0).WithWithdrawals(0).TestObject;
        block.Header.Difficulty = 0;
        block.Header.Nonce = 0;
        block.Header.ParentBeaconBlockRoot = change == "beacon" ? Keccak.Compute("nonzero beacon root") : Keccak.Zero;
        block.Header.RequestsHash = change == "requests" ? Keccak.Compute("nonempty requests") : ExecutionRequestExtensions.EmptyRequestsHash;
        Assert.That(environment.BlockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(EezBlockValidator.ConsensusLayerFieldsNotAllowed));
    }

}
