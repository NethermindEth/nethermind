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

using System;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InvalidHeaderSealInterceptorTest
{
    private class Context
    {
        private BlockHeader _blockHeader = Build.A.BlockHeader.TestObject;
        private ISealValidator _baseValidator = Substitute.For<ISealValidator>();
        private IInvalidChainTracker _invalidChainTracker = Substitute.For<IInvalidChainTracker>();

        public Context OnValidateSeal()
        {
            InvalidHeaderSealInterceptor validator = new(_baseValidator, _invalidChainTracker, LimboLogs.Instance);
            validator.ValidateSeal(_blockHeader, false);
            return this;
        }

        public Context GivenSealIsValid()
        {
            _baseValidator.ValidateSeal(_blockHeader, Arg.Any<bool>()).Returns(true);
            return this;
        }

        public Context GivenSealIsNotValid()
        {
            _baseValidator.ValidateSeal(_blockHeader, Arg.Any<bool>()).Returns(false);
            return this;
        }

        public Context InvalidBlockShouldGetReported()
        {
            _invalidChainTracker.Received().OnInvalidBlock(Arg.Any<Keccak>(), Arg.Any<Keccak>());
            return this;
        }

        public Context InvalidBlockShouldNotGetReported()
        {
            _invalidChainTracker.DidNotReceive().OnInvalidBlock(Arg.Any<Keccak>(), Arg.Any<Keccak>());
            return this;
        }
    }

    [Test]
    public void Test_seal_valid()
    {
        new Context()
            .GivenSealIsValid()
            .OnValidateSeal()
            .InvalidBlockShouldNotGetReported();
    }

    [Test]
    public void Test_seal_not_valid()
    {
        new Context()
            .GivenSealIsNotValid()
            .OnValidateSeal()
            .InvalidBlockShouldGetReported();
    }
}

