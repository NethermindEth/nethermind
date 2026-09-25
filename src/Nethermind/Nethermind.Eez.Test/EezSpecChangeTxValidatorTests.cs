// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSpecChangeTxValidatorTests
{
    private const ulong ChainId = 6290;
    private readonly EezSpecChangeTxValidator _validator = new(ChainId);

    [Test]
    public void Validate_SystemTransaction_IsRejectedOnEveryPoolPath()
    {
        Transaction tx = SystemTransactions.Create(ChainId);

        Assert.That(_validator.IsWellFormed(tx, Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.SystemTransactionNotAllowed),
            "an unsigned system transaction can be forged by anyone, so the pool must never admit one");
        Assert.That(_validator.IsWellFormedAfterFullValidation(tx, Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.SystemTransactionNotAllowed),
            "the pool calls only this method after the block-import validator accepted the transaction");
    }

    [Test]
    public void Validate_BlobTransaction_IsRejectedOnEveryPoolPath()
    {
        Transaction tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(spec: Osaka.Instance).WithChainId(ChainId)
            .SignedAndResolved(new EthereumEcdsa(ChainId), TestItem.PrivateKeyA).TestObject;

        Assert.That(new SpecChangeTxValidator(ChainId).IsWellFormed(tx, Osaka.Instance).AsBool(), Is.True,
            "precondition: the transaction is a well-formed Osaka blob transaction on Ethereum");
        Assert.That(_validator.IsWellFormed(tx, Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.BlobTransactionNotAllowed),
            "EEZ L2 has no blob transactions");
        Assert.That(_validator.IsWellFormedAfterFullValidation(tx, Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.BlobTransactionNotAllowed),
            "EEZ L2 has no blob transactions");
        Assert.That(_validator.IsWellFormedLight(new LightTransaction(tx), Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.BlobTransactionNotAllowed),
            "a persisted blob transaction must not be revived from storage");
    }

    [Test]
    public void Validate_FrameTransactionCarryingBlobs_IsRejected()
    {
        Transaction tx = new() { Type = TxType.FrameTx, ChainId = ChainId, BlobVersionedHashes = [new byte[32]] };

        Assert.That(_validator.IsWellFormed(tx, Osaka.Instance).Error, Is.EqualTo(EezSpecChangeTxValidator.BlobTransactionNotAllowed),
            "blobs are rejected whatever transaction type carries them");
    }

    [Test]
    public void Validate_OrdinaryTransaction_IsAdmitted()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(ChainId).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(1)
            .SignedAndResolved(new EthereumEcdsa(ChainId), TestItem.PrivateKeyA).TestObject;

        Assert.That(_validator.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.True, "ordinary L2 transactions follow the Ethereum rules");
        Assert.That(_validator.IsWellFormedAfterFullValidation(tx, Osaka.Instance).AsBool(), Is.True, "ordinary L2 transactions follow the Ethereum rules");
    }
}
