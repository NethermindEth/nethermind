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

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InvalidBlockInterceptorTest
{
    private IBlockValidator _baseValidator;
    private IInvalidChainTracker _tracker;
    private InvalidBlockInterceptor _invalidBlockInterceptor;

    [SetUp]
    public void Setup()
    {
        _baseValidator = Substitute.For<IBlockValidator>();
        _tracker = Substitute.For<IInvalidChainTracker>();
        _invalidBlockInterceptor = new(
            _baseValidator,
            _tracker,
            NullLogManager.Instance);
    }
        
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateSuggestedBlock(bool baseReturnValue, bool isInvalidBlockReported)
    {
        Block block = Build.A.Block.TestObject;
        _baseValidator.ValidateSuggestedBlock(block).Returns(baseReturnValue);
        _invalidBlockInterceptor.ValidateSuggestedBlock(block);
        
        _tracker.Received().SetChildParent(block.Hash, block.ParentHash);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(block.Hash, block.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(block.Hash, block.ParentHash);
        }
    }
    
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void TestValidateProcessedBlock(bool baseReturnValue, bool isInvalidBlockReported)
    {
        Block block = Build.A.Block.TestObject;
        TxReceipt[] txs = { };
        _baseValidator.ValidateProcessedBlock(block, txs, block).Returns(baseReturnValue);
        _invalidBlockInterceptor.ValidateProcessedBlock(block, txs, block);
        
        _tracker.Received().SetChildParent(block.Hash, block.ParentHash);
        if (isInvalidBlockReported)
        {
            _tracker.Received().OnInvalidBlock(block.Hash, block.ParentHash);
        }
        else
        {
            _tracker.DidNotReceive().OnInvalidBlock(block.Hash, block.ParentHash);
        }
    }
    
}
