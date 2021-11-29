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

using System.Linq;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class BlockValidatorTests
    {
        [Test]
        public void Validation_fails_when_more_uncles_than_allowed()
        {
            TxValidator txValidator = new(ChainId.Mainnet);
            ReleaseSpec releaseSpec = new() { MaximumUncleCount = 0 };
            ISpecProvider specProvider = new CustomSpecProvider((0, releaseSpec));

            BlockValidator blockValidator = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
            bool noiseRemoved = blockValidator.ValidateSuggestedBlock(Build.A.Block.TestObject);
            Assert.True(noiseRemoved);
            
            bool result = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithUncles(Build.A.BlockHeader.TestObject).TestObject);
            Assert.False(result);
        }

        [TestCase(false, false, ExpectedResult = true)]
        [TestCase(false, true, ExpectedResult = true)]
        [TestCase(true, false, ExpectedResult = true)]
        [TestCase(true, true, ExpectedResult = false)]
        public bool Validation_fails_when_gas_limit_exceeded(bool isEip4488Enabled, bool shouldBreak448Rule)
        {
            ReleaseSpec releaseSpec = new() { IsEip4488Enabled = isEip4488Enabled };
            ISpecProvider specProvider = new CustomSpecProvider((0, releaseSpec));

            BlockValidator blockValidator = new(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
            
            Block block = Build.A.Block.WithTransactions(
                Build.A.Transaction.WithData(Block.BaseMaxCallDataPerBlock + Transaction.CallDataPerTxStipend).TestObject,
                Build.A.Transaction.WithData(Transaction.CallDataPerTxStipend + (shouldBreak448Rule ? 1 : 0)).TestObject)
                .TestObject;

            return blockValidator.ValidateSuggestedBlock(block);
        }
    }
}
