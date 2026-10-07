// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class CompositeTxValidatorTests
{
    private const ulong BlockGasLimit = 30_000_000;
    private const TxValidationOptions Options = TxValidationOptions.SkipBlobProofs;

    [Test]
    public void Runs_validators_in_order_until_the_first_failure([Values(0, 1, 2, 3)] int failing)
    {
        List<string> calls = [];
        ITxValidator[] validators =
        [
            new SpecOnlyValidator(calls, "first", failing == 0),
            new FullValidator(calls, "second", failing == 1),
            new SpecOnlyValidator(calls, "third", failing == 2),
        ];

        ValidationResult result = new CompositeTxValidator(validators)
            .IsWellFormed(Build.A.Transaction.TestObject, Osaka.Instance, BlockGasLimit, Options);

        string[] expectedCalls = new[] { "first", "second", "third" }.Take(failing + 1).ToArray();
        Assert.That(calls, Is.EqualTo(expectedCalls));
        Assert.That(result.Error, Is.EqualTo(failing < validators.Length ? expectedCalls[^1] : null));
    }

    /// <summary>Implements only the two-argument overload, which the composite reaches through the interface defaults.</summary>
    private sealed class SpecOnlyValidator(List<string> calls, string name, bool fails) : ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        {
            calls.Add(name);
            return fails ? name : ValidationResult.Success;
        }
    }

    private sealed class FullValidator(List<string> calls, string name, bool fails) : ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
            IsWellFormed(transaction, releaseSpec, 0, TxValidationOptions.None);

        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit, TxValidationOptions options)
        {
            Assert.That((blockGasLimit, options), Is.EqualTo((BlockGasLimit, Options)));
            calls.Add(name);
            return fails ? name : ValidationResult.Success;
        }
    }
}
