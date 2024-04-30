// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders;
using Nethermind.Core.ConsensusRequests;

public class WithdrawalRequestBuilder : BuilderBase<WithdrawalRequest>
{
    public WithdrawalRequestBuilder() => TestObject = new();


    public WithdrawalRequestBuilder WithAmount(ulong amount)
    {
        TestObject.Amount = amount;

        return this;
    }

    public WithdrawalRequestBuilder WithSourceAddress(Address sourceAddress)
    {
        TestObject.SourceAddress = sourceAddress;

        return this;
    }

    public WithdrawalRequestBuilder WithValidatorPubkey(byte[] validatorPubkey)
    {
        TestObject.ValidatorPubkey = validatorPubkey;

        return this;
    }

}
