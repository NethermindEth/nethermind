using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders;

public class WithdrawalBuilder : BuilderBase<Withdrawal>
{
    public WithdrawalBuilder() => TestObject = new();

    public WithdrawalBuilder WithAmount(UInt256 amount)
    {
        TestObject.Amount = amount;

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
