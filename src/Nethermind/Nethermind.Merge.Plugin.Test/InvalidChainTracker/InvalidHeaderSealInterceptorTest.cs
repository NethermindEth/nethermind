// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

