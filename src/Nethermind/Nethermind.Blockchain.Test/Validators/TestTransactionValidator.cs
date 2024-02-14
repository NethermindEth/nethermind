// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Test.Validators
{
    public class TestTxValidator : ITxValidator
    {
        public static TestTxValidator AlwaysValid = new(true);
        public static TestTxValidator NeverValid = new(false);

        private readonly Queue<bool> _validationResults = new();
        private readonly bool? _alwaysSameResult;

        public TestTxValidator(Queue<bool> validationResults)
        {
            _validationResults = validationResults;
        }

        public TestTxValidator(bool validationResult)
        {
            _alwaysSameResult = validationResult;
        }

        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        {
            return _alwaysSameResult ?? _validationResults.Dequeue();
        }
        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? errorMessage)
        {
            errorMessage = null;
            return _alwaysSameResult ?? _validationResults.Dequeue();
        }
    }
}
