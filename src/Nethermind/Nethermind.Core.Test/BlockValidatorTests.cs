/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain.Validators;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockValidatorTests
    {
        [Test]
        public void When_more_uncles_than_allowed_returns_false()
        {
            TxValidator txValidator = new TxValidator(ChainId.MainNet);
            ReleaseSpec releaseSpec = new ReleaseSpec();
            releaseSpec.MaximumUncleCount = 0;
            ISpecProvider specProvider = new CustomSpecProvider((0, releaseSpec));

            BlockValidator blockValidator = new BlockValidator(txValidator, AlwaysValidHeaderValidator.Instance, AlwaysValidOmmersValidator.Instance, specProvider, LimboLogs.Instance);
            bool noiseRemoved = blockValidator.ValidateSuggestedBlock(Build.A.Block.TestObject);
            Assert.True(noiseRemoved);
            
            bool result = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithOmmers(Build.A.BlockHeader.TestObject).TestObject);
            Assert.False(result);
        }
    }
}