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

using FluentAssertions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
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
        
        _tracker.Received().SetChildParent(header.Hash, header.ParentHash);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(header.Hash, header.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(header.Hash, header.ParentHash);
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
        
        _tracker.Received().SetChildParent(header.Hash, header.ParentHash);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(header.Hash, header.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(header.Hash, header.ParentHash);
        }
    }
}
