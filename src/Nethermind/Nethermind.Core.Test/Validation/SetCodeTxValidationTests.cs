// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Validation;
using NUnit.Framework;

namespace Nethermind.Core.Test.Validation;

[Parallelizable(ParallelScope.All)]
public class SetCodeTxValidationTests
{
    private static AuthorizationTuple CreateAuthorizationTuple() =>
        new(0, Address.Zero, 0, new Signature(new byte[64], 0));

    [Test]
    public void ValidateNoContractCreation_returns_success_for_call_transaction()
    {
        Transaction tx = Build.A.Transaction.To(TestItem.AddressA).TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateNoContractCreation(tx);

        Assert.That(result.AsBool(), Is.True);
    }

    [Test]
    public void ValidateNoContractCreation_returns_error_for_contract_creation()
    {
        Transaction tx = Build.A.Transaction.To(null).TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateNoContractCreation(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AsBool(), Is.False);
            Assert.That(result.Error, Is.EqualTo(TxErrorMessages.NotAllowedCreateTransaction));
        }
    }

    private static IEnumerable<TestCaseData> MissingAuthorizationListCases()
    {
        yield return new TestCaseData((Func<Transaction>)(() =>
            Build.A.Transaction.To(TestItem.AddressA).TestObject))
        { TestName = "null_authorization_list" };
        yield return new TestCaseData((Func<Transaction>)(() =>
            Build.A.Transaction
                .To(TestItem.AddressA)
                .WithAuthorizationCode(Array.Empty<AuthorizationTuple>())
                .TestObject))
        { TestName = "empty_authorization_list" };
    }

    [TestCaseSource(nameof(MissingAuthorizationListCases))]
    public void ValidateAuthorizationList_returns_error_when_list_is_missing(Func<Transaction> buildTransaction)
    {
        Transaction tx = buildTransaction();

        ValidationResult result = SetCodeTxValidation.ValidateAuthorizationList(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AsBool(), Is.False);
            Assert.That(result.Error, Is.EqualTo(TxErrorMessages.MissingAuthorizationList));
        }
    }

    [Test]
    public void ValidateAuthorizationList_returns_success_when_list_has_entries()
    {
        Transaction tx = Build.A.Transaction
            .To(TestItem.AddressA)
            .WithAuthorizationCode([CreateAuthorizationTuple()])
            .TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateAuthorizationList(tx);

        Assert.That(result.AsBool(), Is.True);
    }
}
