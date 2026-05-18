// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Validation;
using NUnit.Framework;

namespace Nethermind.Core.Test.Validation;

[Parallelizable(ParallelScope.All)]
public class SetCodeTxValidationTests
{
    private static AuthorizationTuple AuthorizationTuple() =>
        new(0, Address.Zero, 0, new Signature(new byte[64], 0));

    [Test]
    public void ValidateNoContractCreation_returns_success_for_call_transaction()
    {
        Transaction tx = Build.A.Transaction.To(TestItem.AddressA).TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateNoContractCreation(tx);

        result.AsBool().Should().BeTrue();
    }

    [Test]
    public void ValidateNoContractCreation_returns_error_for_contract_creation()
    {
        Transaction tx = Build.A.Transaction.To(null).TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateNoContractCreation(tx);

        result.AsBool().Should().BeFalse();
        result.Error.Should().Be(TxErrorMessages.NotAllowedCreateTransaction);
    }

    [Test]
    public void ValidateAuthorizationList_returns_error_when_list_is_null()
    {
        Transaction tx = Build.A.Transaction.To(TestItem.AddressA).TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateAuthorizationList(tx);

        result.AsBool().Should().BeFalse();
        result.Error.Should().Be(TxErrorMessages.MissingAuthorizationList);
    }

    [Test]
    public void ValidateAuthorizationList_returns_error_when_list_is_empty()
    {
        Transaction tx = Build.A.Transaction
            .To(TestItem.AddressA)
            .WithAuthorizationCode(Array.Empty<AuthorizationTuple>())
            .TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateAuthorizationList(tx);

        result.AsBool().Should().BeFalse();
        result.Error.Should().Be(TxErrorMessages.MissingAuthorizationList);
    }

    [Test]
    public void ValidateAuthorizationList_returns_success_when_list_has_entries()
    {
        Transaction tx = Build.A.Transaction
            .To(TestItem.AddressA)
            .WithAuthorizationCode(new[] { AuthorizationTuple() })
            .TestObject;

        ValidationResult result = SetCodeTxValidation.ValidateAuthorizationList(tx);

        result.AsBool().Should().BeTrue();
    }
}
