//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
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
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            return _alwaysSameResultForParams ?? _suggestedValidationResults.Dequeue();
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            return _alwaysSameResultForSeal ?? _processedValidationResults.Dequeue();
        }
    }
}
