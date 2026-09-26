// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
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
}
