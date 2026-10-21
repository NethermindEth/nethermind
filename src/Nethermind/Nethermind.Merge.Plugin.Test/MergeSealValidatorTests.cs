// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
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

        public void BaseValidateSealShouldBeForced() =>
            _baseValidator.Received().ValidateSeal(_blockHeader, true);
    }

    [Test]
    public void TestTerminalBlockBehaviour() =>
        new Context()
            .WhenHeaderIsTerminalBlock()
            .OnValidateSeal()
            .BaseValidateSealShouldBeForced();

    [Test]
    public void Hint_is_forwarded_through_the_whole_decoration_chain()
    {
        // ISealValidator.HintValidationRange has an empty default implementation, so a decorator that omits it
        // silently swallows the hint and the pre-merge validator never prepares the epoch it is asked to validate.
        ISealValidator preMergeSealValidator = Substitute.For<ISealValidator>();
        ISealValidator decorated = new InvalidHeaderSealInterceptor(
            new MergeSealValidator(Substitute.For<IPoSSwitcher>(), preMergeSealValidator),
            Substitute.For<IInvalidChainTracker>(),
            LimboLogs.Instance);

        Guid guid = Guid.NewGuid();
        decorated.HintValidationRange(guid, 100, 200);

        preMergeSealValidator.Received().HintValidationRange(guid, 100, 200);
    }
}
