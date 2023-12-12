// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeSealValidatorTests
{
    private class Context
    {
        private readonly BlockHeader _blockHeader = Build.A.BlockHeader.TestObject;
        private readonly IPoSSwitcher _poSSwitcher = Substitute.For<IPoSSwitcher>();
        private readonly ISealValidator _baseValidator = Substitute.For<ISealValidator>();

        public Context WhenHeaderIsTerminalBlock()
        {
            _poSSwitcher.GetBlockConsensusInfo(_blockHeader).Returns((true, false));
            return this;
        }

        public Context OnValidateSeal()
        {
            MergeSealValidator validator = new(_poSSwitcher, _baseValidator);
            validator.ValidateSeal(_blockHeader, false);
            return this;
        }

        public void BaseValidateSealShouldBeForced()
        {
            _baseValidator.Received().ValidateSeal(_blockHeader, true);
        }
    }

    [Test]
    public void TestTerminalBlockBehaviour()
    {
        new Context()
            .WhenHeaderIsTerminalBlock()
            .OnValidateSeal()
            .BaseValidateSealShouldBeForced();
    }
}
