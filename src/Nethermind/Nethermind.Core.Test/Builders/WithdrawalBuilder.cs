// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders;

public class WithdrawalBuilder : BuilderBase<Withdrawal>
{
    public WithdrawalBuilder() => TestObject = new();

    public WithdrawalBuilder WithAmount(ulong amount)
    {
        TestObject.AmountInGwei = amount;

        return this;
    }

    public WithdrawalBuilder WithIndex(ulong index)
    {
        TestObject.Index = index;

        return this;
    }

    public WithdrawalBuilder WithRecipient(Address recipient)
    {
        TestObject.Address = recipient;

        return this;
    }

    public WithdrawalBuilder WithValidatorIndex(ulong index)
    {
        TestObject.ValidatorIndex = index;

        return this;
    }
}
