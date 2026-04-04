// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class HeaderValidatorTests
{
    private IHeaderValidator _validator = null!;
    private ISealValidator _ethash = null!;
    private TestLogger _testLogger = null!;
    private Block _parentBlock = null!;
    private Block _block = null!;
    private IBlockTree _blockTree = null!;
    private ISpecProvider _specProvider = null!;

    [SetUp]
    public void Setup()
    {
        EthashDifficultyCalculator calculator = new(new TestSingleReleaseSpecProvider(Frontier.Instance));
        _ethash = new EthashSealValidator(LimboLogs.Instance, calculator, new CryptoRandom(), new Ethash(LimboLogs.Instance), Timestamper.Default);
        _testLogger = new TestLogger();
        _blockTree = Build.A.BlockTree()
            .WithSpecProvider(FrontierSpecProvider.Instance)
            .WithoutSettingHead
            .TestObject;
        _specProvider = new TestSingleReleaseSpecProvider(Byzantium.Instance);

        _validator = new HeaderValidator(_blockTree, _ethash, _specProvider, new OneLoggerLogManager(new(_testLogger)));
        _parentBlock = Build.A.Block.WithDifficulty(1).TestObject;
        _block = Build.A.Block.WithParent(_parentBlock)
            .WithDifficulty(131072)
            .WithMixHash(new Hash256("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
            .WithNonce(0).TestObject;

        _blockTree.SuggestBlock(_parentBlock);
        _blockTree.SuggestBlock(_block);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Valid_when_valid()
    {
        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        if (!result)
        {
            foreach (string error in _testLogger.LogList)
            {
                Console.WriteLine(error);
            }
        }

        Assert.That(result, Is.True);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0, false, TestName = "When_gas_limit_too_high")]
    [TestCase(-1, true, TestName = "When_gas_limit_just_correct_high")]
    public void When_gas_limit_above_parent(int adjustment, bool expectedResult)
    {
        _block.Header.GasLimit = _parentBlock.Header.GasLimit + (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) + adjustment;
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(1, true, TestName = "When_gas_limit_just_correct_low")]
    [TestCase(0, false, TestName = "When_gas_limit_is_just_too_low")]
    public void When_gas_limit_below_parent(int adjustment, bool expectedResult)
    {
        _block.Header.GasLimit = _parentBlock.Header.GasLimit - (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) + adjustment;
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    private static IEnumerable<TestCaseData> InvalidFieldCases()
    {
        yield return new TestCaseData(new Action<Block, Block>((block, parent) => block.Header.GasUsed = parent.Header.GasLimit + 1))
            .SetName("When_gas_used_above_gas_limit");
        yield return new TestCaseData(new Action<Block, Block>((block, _) => block.Header.ParentHash = Keccak.Zero))
            .SetName("When_no_parent_invalid");
        yield return new TestCaseData(new Action<Block, Block>((block, parent) => block.Header.Timestamp = parent.Header.Timestamp))
            .SetName("When_timestamp_same_as_parent");
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCaseSource(nameof(InvalidFieldCases))]
    public void Header_field_invalidation_returns_false(Action<Block, Block> corrupt)
    {
        corrupt(_block, _parentBlock);
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_slot_number_same_as_parent()
    {
        // Arrange: Same setup as above
        TestSpecProvider specProvider = new(Amsterdam.Instance);
        _validator = new HeaderValidator(_blockTree, Always.Valid, specProvider,
            new OneLoggerLogManager(new(_testLogger)));

        BlockAccessList emptyBal = new();
        byte[] emptyEncodedBal = Rlp.Encode(emptyBal).Bytes;
        _parentBlock = Build.A.Block
            .WithNumber(5)
            .WithSlotNumber(10)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithEmptyRequestsHash()
            .WithBlockAccessList(emptyBal)
            .WithEncodedBlockAccessList(emptyEncodedBal)
            .WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp)
            .TestObject;

        _block = Build.A.Block
            .WithParent(_parentBlock)
            .WithNumber(_parentBlock.Number + 1)
            .WithSlotNumber(10)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithEmptyRequestsHash()
            .WithBlockAccessList(emptyBal)
            .WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp)
            .WithEncodedBlockAccessList(emptyEncodedBal)
            .TestObject;

        _block.Header.Hash = _block.CalculateHash();

        // Act
        bool result = _validator.Validate(_block.Header, _parentBlock.Header, false, out string? error);

        using (Assert.EnterMultipleScope())
        {
            // Assert
            Assert.That(result, Is.False);
            Assert.That(error, Is.EqualTo("InvalidSlotNumber: Slot number in header must exceed parent."));
        }
    }

    private static IEnumerable<TestCaseData> CorruptedFieldCases()
    {
        yield return new TestCaseData(new Action<Block>(b => b.Header.ExtraData = new byte[33]))
            .SetName("When_extra_data_too_long");
        yield return new TestCaseData(new Action<Block>(b => b.Header.Difficulty = 1))
            .SetName("When_incorrect_difficulty_then_invalid");
        yield return new TestCaseData(new Action<Block>(b => b.Header.Number += 1))
            .SetName("When_incorrect_number_then_invalid");
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCaseSource(nameof(CorruptedFieldCases))]
    public void Header_field_corruption_returns_false(Action<Block> corrupt)
    {
        corrupt(_block);
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.False);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(10000000, 4, 20000000, true)]
    [TestCase(10000000, 4, 20019530, true)]
    [TestCase(10000000, 4, 20019531, false)]
    [TestCase(10000000, 4, 19980470, true)]
    [TestCase(10000000, 4, 19980469, false)]
    [TestCase(20000000, 5, 20000000, true)]
    [TestCase(20000000, 5, 20019530, true)]
    [TestCase(20000000, 5, 20019531, false)]
    [TestCase(20000000, 5, 19980470, true)]
    [TestCase(20000000, 5, 19980469, false)]
    [TestCase(40000000, 5, 40039061, true)]
    [TestCase(40000000, 5, 40039062, false)]
    [TestCase(40000000, 5, 39960939, true)]
    [TestCase(40000000, 5, 39960938, false)]
    public void When_gaslimit_is_on_london_fork(long parentGasLimit, long blockNumber, long gasLimit, bool expectedResult)
    {
        OverridableReleaseSpec spec = new(London.Instance)
        {
            Eip1559TransitionBlock = 5
        };
        TestSpecProvider specProvider = new(spec);
        _validator = new HeaderValidator(_blockTree, _ethash, specProvider, new OneLoggerLogManager(new(_testLogger)));
        _parentBlock = Build.A.Block.WithDifficulty(1)
                        .WithGasLimit(parentGasLimit)
                        .WithNumber(blockNumber)
                        .TestObject;
        _block = Build.A.Block.WithParent(_parentBlock)
            .WithDifficulty(131072)
            .WithMixHash(new Hash256("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
            .WithGasLimit(gasLimit)
            .WithNumber(_parentBlock.Number + 1)
            .WithBaseFeePerGas(BaseFeeCalculator.Calculate(_parentBlock.Header, specProvider.GetSpec((ForkActivation)(_parentBlock.Number + 1))))
            .WithNonce(0).TestObject;
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(30_000_000, 5, 15_000_000, true, TestName = "Activation_block_exact_half_is_valid")]
    [TestCase(30_000_000, 5, 15_000_001, false, TestName = "Activation_block_above_half_is_invalid")]
    [TestCase(30_000_000, 5, 14_999_999, false, TestName = "Activation_block_below_half_is_invalid")]
    public void When_gaslimit_is_on_eip7782_activation(long parentGasLimit, long forkBlock, long gasLimit, bool expectedResult)
    {
        OverridableReleaseSpec preForkSpec = new(Byzantium.Instance) { IsEip7782Enabled = false };
        OverridableReleaseSpec postForkSpec = new(Byzantium.Instance) { IsEip7782Enabled = true };
        TestSpecProvider specProvider = new(preForkSpec);
        specProvider.NextForkSpec = postForkSpec;
        specProvider.ForkOnBlockNumber = forkBlock;

        _validator = new HeaderValidator(_blockTree, _ethash, specProvider, new OneLoggerLogManager(new(_testLogger)));
        _parentBlock = Build.A.Block.WithDifficulty(1)
            .WithGasLimit(parentGasLimit)
            .WithNumber(forkBlock - 1)
            .TestObject;
        _block = Build.A.Block.WithParent(_parentBlock)
            .WithDifficulty(131072)
            .WithMixHash(new Hash256("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
            .WithGasLimit(gasLimit)
            .WithNumber(forkBlock)
            .WithNonce(0).TestObject;
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_gas_limit_is_long_max_value()
    {
        _validator = new HeaderValidator(_blockTree, _ethash, _specProvider, new OneLoggerLogManager(new(_testLogger)));
        _parentBlock = Build.A.Block.WithDifficulty(1)
            .WithGasLimit(long.MaxValue)
            .WithNumber(5)
            .TestObject;
        _block = Build.A.Block.WithParent(_parentBlock)
            .WithDifficulty(131072)
            .WithMixHash(new Hash256("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
            .WithGasLimit(long.MaxValue)
            .WithNumber(_parentBlock.Number + 1)
            .WithNonce(0).TestObject;
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);

        Assert.That(result, Is.True);
    }

    private static IEnumerable<TestCaseData> NegativeFieldCases()
    {
        yield return new TestCaseData(new Action<Block>(b => b.Header.Number = -1))
            .SetName("When_block_number_is_negative");
        yield return new TestCaseData(new Action<Block>(b => b.Header.GasUsed = -1))
            .SetName("When_gas_used_is_negative");
        yield return new TestCaseData(new Action<Block>(b => b.Header.GasLimit = -1))
            .SetName("When_gas_limit_is_negative");
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCaseSource(nameof(NegativeFieldCases))]
    public void When_header_field_is_negative(Action<Block> corrupt)
    {
        corrupt(_block);
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_total_difficulty_null_we_should_skip_total_difficulty_validation()
    {
        _block.Header.Difficulty = 1;
        _block.Header.TotalDifficulty = null;
        _block.Header.Hash = _block.CalculateHash();

        HeaderValidator validator = new HeaderValidator(_blockTree, Always.Valid, _specProvider, new OneLoggerLogManager(new(_testLogger)));
        bool result = validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.True);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0, 0, true)]
    [TestCase(0, null, false)]
    [TestCase(0, 1, false)]
    [TestCase(1, 0, false)]
    [TestCase(1, null, false)]
    [TestCase(1, 1, false)]
    public void When_total_difficulty_zero_we_should_skip_total_difficulty_validation_depending_on_ttd_and_genesis_td(
            long genesisTd, long? ttd, bool expectedResult)
    {
        _block.Header.Difficulty = 1;
        _block.Header.TotalDifficulty = 0;
        _block.Header.Hash = _block.CalculateHash();

        {
            _blockTree = Build.A.BlockTree()
                .WithSpecProvider(FrontierSpecProvider.Instance)
                .WithoutSettingHead
                .TestObject;

            Block genesis = Build.A.Block.WithDifficulty((UInt256)genesisTd).TestObject;
            _blockTree.SuggestBlock(genesis);
        }

        _specProvider.UpdateMergeTransitionInfo(null, (UInt256?)ttd);

        HeaderValidator validator = new(_blockTree, Always.Valid, _specProvider, new OneLoggerLogManager(new(_testLogger)));
        bool result = validator.Validate(_block.Header, _parentBlock.Header);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    [Test]
    public void Validate_HashIsWrong_ErrorMessageIsSet()
    {
        HeaderValidator sut = new HeaderValidator(_blockTree, Always.Valid, Substitute.For<ISpecProvider>(), new OneLoggerLogManager(new(_testLogger)));
        _block.Header.Hash = Keccak.Zero;
        string? error;
        sut.Validate(_block.Header, _parentBlock.Header, false, out error);

        Assert.That(error, Does.StartWith("InvalidHeaderHash"));
    }

    [Test]
    public void When_given_parent_is_wrong()
    {
        _block.Header.Hash = _block.CalculateHash();

        bool result = _validator.Validate(_block.Header, Build.A.BlockHeader.WithNonce(999).TestObject, false, out string? error);

        Assert.That(result, Is.False);
        Assert.That(error, Does.StartWith("Mismatched parent"));
    }

    [Test]
    public void BaseFee_validation_must_not_be_bypassed_for_NoProof_seal_engine()
    {
        // Arrange: Create London fork with EIP-1559
        OverridableReleaseSpec spec = new(London.Instance)
        {
            Eip1559TransitionBlock = 0
        };
        TestSpecProvider specProvider = new(spec);

        // Create validator with no-op seal validator (simulates NoProof)
        _validator = new HeaderValidator(
            _blockTree,
            Always.Valid,  // No seal validation (NoProof behavior)
            specProvider,
            new OneLoggerLogManager(new(_testLogger))
        );

        // Parent block: baseFee=10, gasUsed=0, gasLimit=300000000
        _parentBlock = Build.A.Block
            .WithDifficulty(0)  // Post-merge
            .WithBaseFeePerGas(10)
            .WithGasUsed(0)
            .WithGasLimit(300000000)
            .WithNumber(5)
            .TestObject;

        // Calculate expected baseFee for child block
        // parentGasTarget = 300000000 / 2 = 150000000
        // gasDelta = 150000000 - 0 = 150000000
        // feeDelta = 10 * 150000000 / 150000000 / 8 = 1
        // expectedBaseFee = 10 - 1 = 9
        UInt256 expectedBaseFee = BaseFeeCalculator.Calculate(
            _parentBlock.Header,
            specProvider.GetSpec((ForkActivation)(_parentBlock.Number + 1))
        );
        Assert.That(expectedBaseFee, Is.EqualTo((UInt256)9), "Test setup: expected baseFee should be 9");

        // Create block with INCORRECT baseFee (10 instead of 9)
        _block = Build.A.Block
            .WithParent(_parentBlock)
            .WithDifficulty(0)
            .WithBaseFeePerGas(10)  // WRONG! Should be 9
            .WithGasUsed(0)
            .WithGasLimit(300000000)
            .WithNumber(_parentBlock.Number + 1)
            .TestObject;

        _block.Header.Hash = _block.CalculateHash();

        // Act: Validate the block
        bool result = _validator.Validate(_block.Header, _parentBlock.Header);

        // Assert: Block MUST be rejected due to invalid baseFee
        // Even though seal engine is NoProof, consensus rules must be enforced
        Assert.That(result, Is.False,
            "Block with invalid baseFee must be rejected even with NoProof seal engine. " +
            $"Expected baseFee=9, actual baseFee=10");

        // Verify the error message mentions baseFee
        bool baseFeeErrorLogged = _testLogger.LogList.Any(log =>
            log.Contains("base fee", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("baseFee", StringComparison.OrdinalIgnoreCase));
        Assert.That(baseFeeErrorLogged, Is.True,
            "Validation should log baseFee mismatch error");
    }

    [Test]
    public void Valid_baseFee_with_NoProof_seal_engine_should_pass()
    {
        // Arrange: Same setup as above
        OverridableReleaseSpec spec = new(London.Instance)
        {
            Eip1559TransitionBlock = 0
        };
        TestSpecProvider specProvider = new(spec);
        _validator = new HeaderValidator(_blockTree, Always.Valid, specProvider,
            new OneLoggerLogManager(new(_testLogger)));

        _parentBlock = Build.A.Block
            .WithDifficulty(0)
            .WithBaseFeePerGas(10)
            .WithGasUsed(0)
            .WithGasLimit(300000000)
            .WithNumber(5)
            .TestObject;

        // Calculate CORRECT baseFee
        UInt256 correctBaseFee = BaseFeeCalculator.Calculate(
            _parentBlock.Header,
            specProvider.GetSpec((ForkActivation)(_parentBlock.Number + 1))
        );

        // Create block with CORRECT baseFee (9)
        _block = Build.A.Block
            .WithParent(_parentBlock)
            .WithDifficulty(0)
            .WithBaseFeePerGas(correctBaseFee)  // CORRECT: 9
            .WithGasUsed(0)
            .WithGasLimit(300000000)
            .WithNumber(_parentBlock.Number + 1)
            .TestObject;

        _block.Header.Hash = _block.CalculateHash();

        // Act
        bool result = _validator.Validate(_block.Header, _parentBlock.Header);

        // Assert: Valid block should pass
        Assert.That(result, Is.True,
            "Block with correct baseFee should be accepted with NoProof seal engine");
    }
}
