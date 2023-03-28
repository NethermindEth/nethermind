// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class BlockValidatorTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_more_uncles_than_allowed_returns_false()
        {
            ReleaseSpec releaseSpec = new();
            releaseSpec.MaximumUncleCount = 0;
            ISpecProvider specProvider = new CustomSpecProvider(TestBlockchainIds.ChainId, TestBlockchainIds.ChainId, ((ForkActivation)0, releaseSpec));
            TxValidator txValidator = new(specProvider);
            BlockValidator blockValidator = new(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance);
            bool noiseRemoved = blockValidator.ValidateSuggestedBlock(Build.A.Block.TestObject);
            Assert.True(noiseRemoved);

            bool result = blockValidator.ValidateSuggestedBlock(Build.A.Block.WithUncles(Build.A.BlockHeader.TestObject).TestObject);
            Assert.False(result);
        }
    }
}
