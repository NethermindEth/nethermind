// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TransactionTests
{
    [Test]
    public void CopyTo_should_preserve_legacy_CLR_signature_and_expose_explicit_hash_control()
    {
        MethodInfo? legacyCopy = typeof(Transaction).GetMethod(nameof(Transaction.CopyTo), [typeof(Transaction)]);
        MethodInfo? explicitCopy = typeof(Transaction).GetMethod(nameof(Transaction.CopyTo), [typeof(Transaction), typeof(bool)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(legacyCopy, Is.Not.Null);
            Assert.That(explicitCopy, Is.Not.Null);
            Assert.That(explicitCopy?.GetParameters()[1].HasDefaultValue, Is.False);
        }
    }

    [Test]
    public void ShardBlobNetworkWrapper_should_preserve_legacy_constructor_and_deconstructor()
    {
        Type wrapperType = typeof(ShardBlobNetworkWrapper);
        Type[] constructorParameters = [typeof(byte[][]), typeof(byte[][]), typeof(byte[][]), typeof(ProofVersion)];
        Type[] deconstructorParameters =
        [
            typeof(byte[][]).MakeByRefType(),
            typeof(byte[][]).MakeByRefType(),
            typeof(byte[][]).MakeByRefType(),
            typeof(ProofVersion).MakeByRefType(),
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapperType.GetConstructor(constructorParameters), Is.Not.Null);
            Assert.That(wrapperType.GetMethod("Deconstruct", deconstructorParameters), Is.Not.Null);
        }
    }

    [Test]
    public void When_to_not_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = Address.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.True, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.False, nameof(Transaction.IsContractCreation));
        }
    }

    [Test]
    public void When_to_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.False, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.True, nameof(Transaction.IsContractCreation));
        }
    }

    [TestCase(1, true)]
    [TestCase(300, true)]
    public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
    {
        Transaction transaction = new();
        transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
        transaction.Type = TxType.EIP1559;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
            Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
        }
    }
}
