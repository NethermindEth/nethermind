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

using DotNetty.Codecs;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Specs;
using NLog;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeHeaderValidatorTest
{
    private IPoSSwitcher _poSSwitcher;
    private IHeaderValidator _baseValidator;
    private IBlockTree _blockTree;
    private ISealValidator _sealValidator;
    private ISpecProvider _specProvider;
    private IMergeConfig _mergeConfig;

    [SetUp]
    public void Setup()
    {
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _blockTree = Build.A.BlockTree().TestObject;
        _sealValidator = NullSealEngine.Instance;
        _mergeConfig = Substitute.For<IMergeConfig>();
        _specProvider = MainnetSpecProvider.Instance;
        _baseValidator = Substitute.For<IHeaderValidator>();
    }

    private MergeHeaderValidator CreateValidator()
    {
        return new MergeHeaderValidator(
            _poSSwitcher,
            _baseValidator,
            _blockTree,
            _specProvider,
            _sealValidator,
            _mergeConfig,
            LimboLogs.Instance);
    }

    [Test]
    public void TestTerminalBlockHash_mismatch()
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        MergeHeaderValidator mergeHeaderValidator = CreateValidator();

        GivenNextBlockIsTerminal();
        GivenConfiguredTerminalBlockHash(Keccak.Compute("something else"));
        GivenBaseValidatorAlwaysPass();
        mergeHeaderValidator.Validate(blockHeader)
            .Should()
            .BeFalse();
    }

    [Test]
    public void TestTerminalBlockHash_match()
    {
        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;

        MergeHeaderValidator mergeHeaderValidator = CreateValidator();

        GivenNextBlockIsTerminal();
        GivenConfiguredTerminalBlockHash(blockHeader.Hash);
        GivenBaseValidatorAlwaysPass();
        mergeHeaderValidator.Validate(blockHeader)
            .Should()
            .BeTrue();
    }

    private void GivenNextBlockIsTerminal()
    {
        _poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>())
            .Returns(false);
        _poSSwitcher.GetBlockConsensusInfo(Arg.Any<BlockHeader>())
            .Returns((true, false));
    }

    private void GivenConfiguredTerminalBlockHash(Keccak hash)
    {
        _mergeConfig.TerminalBlockHashParsed.Returns(hash);
    }

    private void GivenBaseValidatorAlwaysPass()
    {
        _baseValidator.Validate(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()).Returns(true);
    }
}
