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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Potocol;
using Nethermind.Mining;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    [Ignore("not yet ready")]
    public class BlockValidatorTests
    {
        [Test]
        public void Test()
        {
            IEthereumRelease spec = Olympic.Instance;
            IBlockStore blockchain = Substitute.For<IBlockStore>();

            BlockHeaderValidator blockHeaderValidator = new BlockHeaderValidator(blockchain, new Ethash());
            OmmersValidator ommersValidator = new OmmersValidator(blockchain, blockHeaderValidator);
            SignatureValidator signatureValidator = new SignatureValidator(spec, ChainId.MainNet);
            TransactionValidator transactionValidator = new TransactionValidator(spec, signatureValidator);
            BlockValidator blockValidator = new BlockValidator(transactionValidator, blockHeaderValidator, ommersValidator, null);
        }
    }
}