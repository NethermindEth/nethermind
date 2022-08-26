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
//
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
        private BlockHeader _blockHeader = Build.A.BlockHeader.TestObject;
        private IPoSSwitcher _poSSwitcher = Substitute.For<IPoSSwitcher>();
        private ISealValidator _baseValidator = Substitute.For<ISealValidator>();

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
