// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders;

public class DepositBuilder : BuilderBase<Deposit>
{
    public DepositBuilder() => TestObject = new();

    public DepositBuilder WithAmount(ulong amount)
    {
        TestObject.Amount = amount;

        return this;
    }

    public DepositBuilder WithIndex(ulong index)
    {
        TestObject.Index = index;

        return this;
    }

    public DepositBuilder WithWithdrawalCredentials(byte[] withdrawalCredentials)
    {
        TestObject.WithdrawalCredentials = withdrawalCredentials;

        return this;
    }

    public DepositBuilder WithSignature(byte[] signature)
    {
        TestObject.Signature = signature;

        return this;
    }
    public DepositBuilder WithPublicKey(byte[] pubKey)
    {
        TestObject.PubKey = pubKey;

        return this;
    }
}
