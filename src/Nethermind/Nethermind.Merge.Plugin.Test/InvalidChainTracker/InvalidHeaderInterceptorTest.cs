// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InvalidHeaderInterceptorTest
{
    private IHeaderValidator _baseValidator;
    private IInvalidChainTracker _tracker;
    private InvalidHeaderInterceptor _invalidHeaderInterceptor;

    [SetUp]
    public void Setup()
    {
        _baseValidator = Substitute.For<IHeaderValidator>();
        _tracker = Substitute.For<IInvalidChainTracker>();
        _invalidHeaderInterceptor = new(
            _baseValidator,
            _tracker,
            NullLogManager.Instance);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateHeader(bool baseReturnValue, bool isInvalidBlockReported)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        _baseValidator.Validate(header, false).Returns(baseReturnValue);
        _invalidHeaderInterceptor.Validate(header, false);

        _tracker.Received().SetChildParent(header.GetOrCalculateHash(), header.ParentHash!);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(header.GetOrCalculateHash(), header.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(header.GetOrCalculateHash(), header.ParentHash);
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateHeaderWithParent(bool baseReturnValue, bool isInvalidBlockReported)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .TestObject;

        _baseValidator.Validate(header, parent, false).Returns(baseReturnValue);
        _invalidHeaderInterceptor.Validate(header, parent, false);

        _tracker.Received().SetChildParent(header.GetOrCalculateHash(), header.ParentHash!);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(header.GetOrCalculateHash(), header.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(header.GetOrCalculateHash(), header.ParentHash);
        }
    }

    [Test]
    public void TestInvalidBlockhashShouldNotGetTracked()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .TestObject;

        header.StateRoot = Keccak.Zero;

        _baseValidator.Validate(header, parent, false).Returns(false);
        _invalidHeaderInterceptor.Validate(header, parent, false);

        _tracker.DidNotReceive().SetChildParent(header.GetOrCalculateHash(), header.ParentHash!);
        _tracker.DidNotReceive().OnInvalidBlock(header.GetOrCalculateHash(), header.ParentHash);
    }
}
