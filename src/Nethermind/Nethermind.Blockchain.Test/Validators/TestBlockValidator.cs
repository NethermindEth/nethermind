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
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Validators
{
    public class TestBlockValidator : IBlockValidator
    {
        public static TestBlockValidator AlwaysValid = new TestBlockValidator(true, true);
        public static TestBlockValidator NeverValid = new TestBlockValidator(false, false);
        private readonly Queue<bool> _processedValidationResults;

        private readonly Queue<bool> _suggestedValidationResults;
        private bool? _alwaysSameResultForProcessed;

        private bool? _alwaysSameResultForSuggested;

        public TestBlockValidator()
            : this(true, true)
        {
        }

        public TestBlockValidator(bool suggestedValidationResult, bool processedValidationResult)
        {
            _alwaysSameResultForSuggested = suggestedValidationResult;
            _alwaysSameResultForProcessed = processedValidationResult;
        }

        public TestBlockValidator(Queue<bool> suggestedValidationResults, Queue<bool> processedValidationResults)
        {
            _suggestedValidationResults = suggestedValidationResults ?? throw new ArgumentNullException(nameof(suggestedValidationResults));
            _processedValidationResults = processedValidationResults ?? throw new ArgumentNullException(nameof(processedValidationResults));
        }

        public bool ValidateHash(BlockHeader header)
        {
            return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        }

        public bool Validate(BlockHeader header, BlockHeader parent, bool isOmmer)
        {
            return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        }

        public bool Validate(BlockHeader header, bool isOmmer)
        {
            return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            return _alwaysSameResultForSuggested ?? _suggestedValidationResults.Dequeue();
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            return _alwaysSameResultForProcessed ?? _processedValidationResults.Dequeue();
        }
    }
}
