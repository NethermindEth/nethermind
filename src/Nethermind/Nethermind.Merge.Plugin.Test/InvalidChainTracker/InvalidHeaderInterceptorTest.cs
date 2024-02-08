// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private IHeaderValidator _baseValidator = null!;
#pragma warning disable NUnit1032
    private IInvalidChainTracker _tracker = null!;
#pragma warning restore NUnit1032
    private InvalidHeaderInterceptor _invalidHeaderInterceptor = null!;

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

    [TearDown]
    public void TearDown() => (_invalidHeaderInterceptor as IDisposable)?.Dispose();

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateHeader(bool baseReturnValue, bool isInvalidBlockReported)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        string? error;
        _baseValidator.Validate(header, false, out error).Returns(baseReturnValue);
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
        string? error;
        _baseValidator.Validate(header, parent, false, out error).Returns(baseReturnValue);
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
