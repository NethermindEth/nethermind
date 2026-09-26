// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Eez.Execution;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezTxTypeTests
{
    private const ulong ChainId = 6290;

    private TxValidator _txValidator = null!;

    [SetUp]
    public void Setup()
    {
        _txValidator = new TxValidator(ChainId);
        _txValidator.RegisterValidator(EezConstants.SystemTxType, EezTxType.CreateValidator(ChainId));
    }

    [Test]
    public void IsWellFormed_SystemTransaction_IsAcceptedOnlyOnceRegistered()
    {
        Assert.That(new TxValidator(ChainId).IsWellFormed(SystemTransaction(), Osaka.Instance).AsBool(), Is.False,
            "precondition: block import rejects the type until the plugin registers it");
        Assert.That(_txValidator.IsWellFormed(SystemTransaction(), Osaka.Instance).AsBool(), Is.True,
            "an unsigned system transaction is well formed once its validator is registered");
    }

    [Test]
    public void IsWellFormed_SystemTransactionForOtherChain_IsRejected()
    {
        Transaction tx = SystemTransaction();
        tx.ChainId = ChainId + 1;

        Assert.That(_txValidator.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.False, "system transactions keep the chain id check");
    }

    [TestCase(49_475, true, TestName = "IsWellFormed_CalldataFloorWithinBudget_IsAccepted")]
    [TestCase(49_476, false, TestName = "IsWellFormed_CalldataFloorBeyondBudget_IsRejected")]
    public void IsWellFormed_SystemTransactionCalldata_IsBoundByTheEip7623FloorOfItsBudget(int nonZeroBytes, bool expectedValid)
    {
        byte[] data = new byte[nonZeroBytes];
        Array.Fill(data, (byte)0xff);
        Transaction tx = SystemTransactions.Create(ChainId, data: data);

        Assert.That(_txValidator.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.EqualTo(expectedValid),
            "the EIP-7623 calldata floor, not only the standard intrinsic cost, must fit the fixed 2M budget");
    }

    [Test]
    public void IsWellFormed_SystemTransactionWithMaximumNonce_IsRejected()
    {
        Transaction tx = SystemTransaction();
        tx.Nonce = ulong.MaxValue;

        Assert.That(_txValidator.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.False, "EIP-2681 caps the nonce of every transaction");
    }

    private static Transaction SystemTransaction() => SystemTransactions.Create(ChainId);
}
