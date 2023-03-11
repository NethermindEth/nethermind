// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Validators
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class TestSealValidator : ISealValidator
    {
        public static ISealValidator AlwaysValid = Always.Valid;
        public static ISealValidator NeverValid = Always.Invalid;

        private readonly Queue<bool> _processedValidationResults;
        private readonly Queue<bool> _suggestedValidationResults;
        private bool? _alwaysSameResultForSeal;
        private bool? _alwaysSameResultForParams;

        public TestSealValidator(bool validateParamsResult, bool validateSealResult)
        {
            _alwaysSameResultForParams = validateParamsResult;
            _alwaysSameResultForSeal = validateSealResult;
        }

        public TestSealValidator(Queue<bool> suggestedValidationResults, Queue<bool> processedValidationResults)
        {
            _suggestedValidationResults = suggestedValidationResults ?? throw new ArgumentNullException(nameof(suggestedValidationResults));
            _processedValidationResults = processedValidationResults ?? throw new ArgumentNullException(nameof(processedValidationResults));
        }

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle)
        {
            return _alwaysSameResultForParams ?? _suggestedValidationResults.Dequeue();
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            return _alwaysSameResultForSeal ?? _processedValidationResults.Dequeue();
        }
    }
}
