// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaSealValidatorTests
    {
        private AuRaSealValidator _sealValidator;
        private AuRaChainSpecEngineParameters _auRaParameters;
        private IAuRaStepCalculator _auRaStepCalculator;
        private ILogManager _logManager;
        private IWallet _wallet;
        private Address _address;
        private IEthereumEcdsa _ethereumEcdsa;
        private static ulong _currentStep;
        private IReportingValidator _reportingValidator;
        private IBlockTree _blockTree;
        private IValidSealerStrategy _validSealerStrategy;
        private IValidatorStore _validatorStore;

        [SetUp]
        public void SetUp()
        {
            _auRaParameters = new AuRaChainSpecEngineParameters();
            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _logManager = LimboLogs.Instance;
            _wallet = new DevWallet(new WalletConfig(), _logManager);
            _address = _wallet.NewAccount(new NetworkCredential(string.Empty, "AAA").SecurePassword);

            _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
            _currentStep = 11UL;
            _auRaStepCalculator.CurrentStep.Returns(_currentStep);

            _reportingValidator = Substitute.For<IReportingValidator>();
            _blockTree = Substitute.For<IBlockTree>();
            _validSealerStrategy = Substitute.For<IValidSealerStrategy>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _validatorStore.GetValidatorsInfo(Arg.Any<ulong?>()).Returns(new ValidatorInfo(1, 0, []));
            _sealValidator = new AuRaSealValidator(_auRaParameters,
                _auRaStepCalculator,
                _blockTree,
                _validatorStore,
                _validSealerStrategy,
                _ethereumEcdsa,
                new Lazy<IReportingValidator>(_reportingValidator),
                _logManager);
        }

        public enum Repeat
        {
            No,
            Yes,
            YesChangeHash
        }

        private static IEnumerable ValidateParamsTests
        {
            get
            {
                ulong step = 10UL;
                ulong parentStep = 9UL;

                BlockHeaderBuilder GetBlock(ulong? blockStep = null) => Build.A.BlockHeader
                        .WithAura(blockStep ?? step, new byte[Signature.Size])
                        .WithBeneficiary(TestItem.AddressA)
                        .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(parentStep, blockStep ?? step));

                BlockHeaderBuilder GetParentBlock() => Build.A.BlockHeader
                    .WithAura(parentStep, new byte[Signature.Size])
                    .WithBeneficiary(TestItem.AddressB);

                TestCaseData GetTestCaseData(
                    BlockHeaderBuilder parent,
                    BlockHeaderBuilder block,
                    Action<AuRaChainSpecEngineParameters> paramAction = null,
                    Repeat repeat = Repeat.No,
                    bool parentIsHead = true,
                    bool isValidSealer = true,
                    bool isSealedByBeneficiary = true) =>
                    new(parent.TestObject, block.TestObject, paramAction, repeat, parentIsHead, isValidSealer, isSealedByBeneficiary);


                yield return GetTestCaseData(GetParentBlock(), GetBlock())
                    .Returns((true, (object)null)).SetName("General valid.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(step, null))
                    .Returns((false, (object)null)).SetName("Missing AuRaSignature").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(parentStep))
                    .Returns((false, IReportingValidator.MaliciousCause.DuplicateStep)).SetName("Duplicate block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(parentStep - 1))
                    .Returns((false, IReportingValidator.MaliciousCause.DuplicateStep)).SetName("Past block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(parentStep + 7))
                    .Returns((false, IReportingValidator.BenignCause.FutureBlock)).SetName("Future block.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(parentStep + 3))
                    .Returns((true, IReportingValidator.BenignCause.SkippedStep)).SetName("Skipped steps.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(AuraDifficultyCalculator.MaxDifficulty))
                    .Returns((false, (object)null)).SetName("Difficulty too large.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000))
                    .Returns((false, (object)null)).SetName("Wrong difficulty.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000), a => a.ValidateScoreTransition = 100)
                    .Returns((true, (object)null)).SetName("Skip difficulty validation due to ValidateScoreTransition.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep - 1, new byte[Signature.Size]), a => a.ValidateScoreTransition = a.ValidateStepTransition = 100)
                    .Returns((true, (object)null)).SetName("Skip step validation due to ValidateStepTransition.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), repeat: Repeat.Yes)
                    .Returns((true, (object)null)).SetName("Same block twice.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), repeat: Repeat.YesChangeHash)
                    .Returns((true, IReportingValidator.MaliciousCause.SiblingBlocksInSameStep)).SetName("Sibling in same step.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), parentIsHead: false, isValidSealer: false)
                    .Returns((true, (object)null)).SetName("Cannot validate sealer").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), parentIsHead: true, isValidSealer: false)
                    .Returns((false, (object)null)).SetName("Wrong sealer").SetCategory("ValidParams");

                foreach (ulong blockStep in new[] { parentStep, parentStep - 1, parentStep + 7, parentStep + 3 })
                {
                    yield return GetTestCaseData(GetParentBlock(), GetBlock(blockStep), isSealedByBeneficiary: false)
                        .Returns((false, (object)null)).SetName($"Not signed by beneficiary, step {blockStep}.").SetCategory("ValidParams");

                    yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(blockStep, new byte[Signature.Size]))
                        .Returns((false, (object)null)).SetName($"Step {blockStep} does not match the signed difficulty.").SetCategory("ValidParams");
                }

                yield return GetTestCaseData(GetParentBlock(), GetBlock(), repeat: Repeat.YesChangeHash, isSealedByBeneficiary: false)
                    .Returns((false, (object)null)).SetName("Sibling not signed by beneficiary.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(step, []))
                    .Returns((false, (object)null)).SetName("Short AuRaSignature").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock(parentStep + 3), parentIsHead: false, isValidSealer: false)
                    .Returns((true, (object)null)).SetName("Skipped steps, cannot validate sealer").SetCategory("ValidParams");
            }
        }

        [TestCaseSource(nameof(ValidateParamsTests))]
        public (bool, object) validate_params(BlockHeader parentBlock, BlockHeader block, Action<AuRaChainSpecEngineParameters> modifyParameters, Repeat repeat, bool parentIsHead, bool isValidSealer, bool isSealedByBeneficiary)
        {
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(isSealedByBeneficiary ? block.Beneficiary : TestItem.AddressC);
            _blockTree.Head.Returns(parentIsHead ? new Block(parentBlock) : new Block(Build.A.BlockHeader.WithNumber(parentBlock.Number - 1).TestObject));
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), block.Beneficiary, block.GetAuRaStepOrZero(), out _).Returns(isValidSealer);

            object cause = null;

            _reportingValidator.ReportBenign(Arg.Any<Address>(), Arg.Any<ulong>(), Arg.Do<IReportingValidator.BenignCause>(c => cause ??= c));
            _reportingValidator.ReportMalicious(Arg.Any<Address>(), Arg.Any<ulong>(), Arg.Any<byte[]>(), Arg.Do<IReportingValidator.MaliciousCause>(c => cause ??= c));
            BlockHeader header = null, parent = null;
            _reportingValidator.TryReportSkipped(Arg.Do<BlockHeader>(h => header = h), Arg.Do<BlockHeader>(h => parent = h));

            modifyParameters?.Invoke(_auRaParameters);
            bool validateParams = _sealValidator.ValidateParams(parentBlock, block);

            if (header?.GetAuRaStep() > parent?.GetAuRaStep() + 1)
            {
                _reportingValidator.ReportBenign(header.Beneficiary, header.Number, IReportingValidator.BenignCause.SkippedStep);
            }

            if (repeat != Repeat.No)
            {
                if (repeat == Repeat.YesChangeHash)
                {
                    block.Hash = Keccak.Compute("AAA");
                }

                validateParams = _sealValidator.ValidateParams(parentBlock, block);
            }

            return (validateParams, cause);
        }

        [Test]
        public void validate_params_out_of_order()
        {
            _auRaStepCalculator.CurrentStep.Returns(15UL);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), Arg.Any<Address>(), Arg.Any<ulong>(), out _).Returns(true);
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(TestItem.AddressA);
            _blockTree.Head.Returns(new Block(Build.A.BlockHeader.WithNumber(100).TestObject));
            _blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
            _validatorStore.GetValidatorsInfo(Arg.Any<ulong?>()).Returns(new ValidatorInfo(1, 0, [TestItem.AddressA, TestItem.AddressB]));

            object cause = null;
            _reportingValidator.ReportMalicious(Arg.Any<Address>(), Arg.Any<ulong>(), Arg.Any<byte[]>(),
                Arg.Do<IReportingValidator.MaliciousCause>(c => cause ??= c));

            // step 15 arrives first
            BlockHeader parent14 = Build.A.BlockHeader.WithAura(14, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            BlockHeader block15 = Build.A.BlockHeader
                .WithAura(15, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(14, 15))
                .TestObject;

            // step 12 arrives second
            BlockHeader parent11 = Build.A.BlockHeader.WithAura(11, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            BlockHeader block12 = Build.A.BlockHeader
                .WithAura(12, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(11, 12))
                .TestObject;

            // step 13 arrives third — triggers ClearOldCache
            BlockHeader parent12 = Build.A.BlockHeader.WithAura(12, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            BlockHeader block13 = Build.A.BlockHeader
                .WithAura(13, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(12, 13))
                .TestObject;

            // sibling at step 15 with different hash — must be detected
            BlockHeader sibling15 = Build.A.BlockHeader
                .WithAura(15, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(14, 15))
                .TestObject;
            sibling15.Hash = Keccak.Compute("sibling15");

            _sealValidator.ValidateParams(parent14, block15); // [15]
            _sealValidator.ValidateParams(parent11, block12); // [15,12]
            _sealValidator.ValidateParams(parent12, block13); // [15,12,13] → ClearOldCache may wrongly evict step 15
            _sealValidator.ValidateParams(parent14, sibling15);

            Assert.That(cause, Is.EqualTo(IReportingValidator.MaliciousCause.SiblingBlocksInSameStep));
        }

        [Test]
        public void validate_params_checks_proposer_on_a_main_chain_parent_below_head([Values] bool isValidSealer)
        {
            BlockHeader parent = Build.A.BlockHeader.WithNumber(5).WithAura(9, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            BlockHeader block = Build.A.BlockHeader.WithNumber(6).WithParent(parent).WithAura(10, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(9, 10)).TestObject;
            _blockTree.Head.Returns(new Block(Build.A.BlockHeader.WithNumber(8).TestObject));
            _blockTree.IsMainChain(parent).Returns(true);
            _validatorStore.GetValidatorsInfo(Arg.Any<ulong?>()).Returns(new ValidatorInfo(1, 0, [TestItem.AddressA, TestItem.AddressB]));
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(TestItem.AddressA);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), TestItem.AddressA, 10, out _).Returns(isValidSealer);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_sealValidator.ValidateParams(parent, block), Is.EqualTo(isValidSealer));
                Assert.That(_sealValidator.CanCreditChild(parent, block), Is.EqualTo(isValidSealer));
                _validatorStore.Received().GetValidatorsInfo(parent.Number + 1);
            }
        }

        private static readonly Address[] ThreeValidators = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];

        private BlockHeader BranchHeader(ulong number, BlockHeader parent, ulong step) => Build.A.BlockHeader
            .WithNumber(number).WithParentHash(parent.Hash!).WithAura(step, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB)
            .WithExtraData([(byte)number]).TestObject;

        private BlockHeader ChildOf(BlockHeader parent, ulong parentStep) => Build.A.BlockHeader
            .WithNumber(parent.Number + 1).WithParentHash(parent.Hash!).WithAura(parentStep + 1, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
            .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(parentStep, parentStep + 1)).TestObject;

        private void SetUpWrongProposer(ulong headNumber, ulong finalizing = 1)
        {
            _blockTree.Head.Returns(new Block(Build.A.BlockHeader.WithNumber(headNumber).WithExtraData([0xff]).TestObject));
            _validatorStore.GetValidatorsInfo(Arg.Any<ulong?>()).Returns(new ValidatorInfo(finalizing, 0, ThreeValidators));
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(TestItem.AddressA);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), TestItem.AddressA, Arg.Any<ulong>(), out _).Returns(false);
        }

        [Test]
        public void validate_params_rejects_wrong_proposer_on_a_side_branch_shorter_than_the_quorum()
        {
            SetUpWrongProposer(20);
            BlockHeader forkPoint = Build.A.BlockHeader.WithNumber(10).WithAura(5, new byte[Signature.Size]).TestObject;
            BlockHeader parent = BranchHeader(11, forkPoint, 6);
            _blockTree.IsMainChain(forkPoint).Returns(true);
            _blockTree.FindHeader(parent.ParentHash, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(forkPoint);

            bool valid = _sealValidator.ValidateParams(parent, ChildOf(parent, 6));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(valid, Is.False);
                _validatorStore.Received().GetValidatorsInfo(11);
            }
        }

        [Test]
        public void validate_params_leaves_proposer_of_a_side_branch_at_the_quorum_to_block_processing()
        {
            SetUpWrongProposer(20);
            BlockHeader forkPoint = Build.A.BlockHeader.WithNumber(10).WithAura(5, new byte[Signature.Size]).TestObject;
            BlockHeader branch11 = BranchHeader(11, forkPoint, 6);
            BlockHeader parent = BranchHeader(12, branch11, 7);
            _blockTree.IsMainChain(forkPoint).Returns(true);
            _blockTree.FindHeader(parent.ParentHash, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(branch11);
            _blockTree.FindHeader(branch11.ParentHash, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(forkPoint);

            Assert.That(_sealValidator.ValidateParams(parent, ChildOf(parent, 7)), Is.True);
        }

        [Test]
        public void validate_params_leaves_proposer_of_a_side_branch_to_block_processing_when_validators_may_change([Values] bool pending)
        {
            SetUpWrongProposer(20);
            if (pending)
            {
                _validatorStore.PendingValidators.Returns(new PendingValidators(15, TestItem.KeccakA, ThreeValidators));
            }
            else
            {
                _validatorStore.GetValidatorsInfo(null).Returns(new ValidatorInfo(15, 1, ThreeValidators));
            }

            BlockHeader forkPoint = Build.A.BlockHeader.WithNumber(10).WithAura(5, new byte[Signature.Size]).TestObject;
            BlockHeader parent = BranchHeader(11, forkPoint, 6);
            _blockTree.IsMainChain(forkPoint).Returns(true);
            _blockTree.FindHeader(parent.ParentHash, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(forkPoint);

            Assert.That(_sealValidator.ValidateParams(parent, ChildOf(parent, 6)), Is.True);
        }

        [Test]
        public void validate_params_checks_proposer_on_a_parent_just_above_the_head([Values] bool withinQuorum)
        {
            SetUpWrongProposer(20);
            BlockHeader head = _blockTree.Head!.Header;
            BlockHeader parent = withinQuorum ? BranchHeader(21, head, 6) : BranchHeader(22, BranchHeader(21, head, 5), 6);
            _blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Is<ulong?>(20UL)).Returns(head);

            Assert.That(_sealValidator.ValidateParams(parent, ChildOf(parent, 6)), Is.EqualTo(!withinQuorum));
        }

        [Test]
        public void validate_params_checks_proposer_above_head_when_validators_are_a_fixed_list([Values] bool isExpectedProposer)
        {
            _auRaParameters.ValidatorsJson = new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [TestItem.AddressA, TestItem.AddressB] };
            AuRaSealValidator sealValidator = new(_auRaParameters, _auRaStepCalculator, _blockTree, _validatorStore, new ValidSealerStrategy(),
                _ethereumEcdsa, new Lazy<IReportingValidator>(_reportingValidator), _logManager);
            Address proposer = isExpectedProposer ? TestItem.AddressA : TestItem.AddressB;
            BlockHeader parent = Build.A.BlockHeader.WithNumber(50).WithAura(9, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            BlockHeader block = Build.A.BlockHeader.WithNumber(51).WithParent(parent).WithAura(10, new byte[Signature.Size]).WithBeneficiary(proposer)
                .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(9, 10)).TestObject;
            _blockTree.Head.Returns(new Block(Build.A.BlockHeader.WithNumber(0).TestObject));
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(proposer);

            Assert.That(sealValidator.ValidateParams(parent, block), Is.EqualTo(isExpectedProposer));
        }

        [Test]
        public void Seal_then_params_validation_of_a_batch_recovers_each_sealer_once()
        {
            const int batchSize = 128;
            _ethereumEcdsa.RecoverAddress(default, default(ValueHash256)).ReturnsForAnyArgs(TestItem.AddressA);
            _blockTree.Head.Returns(new Block(Build.A.BlockHeader.WithNumber(0).TestObject));
            BlockHeader parent = Build.A.BlockHeader.WithNumber(100).WithAura(1000, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressB).TestObject;
            List<BlockHeader> batch = [];
            for (int i = 0; i < batchSize; i++)
            {
                batch.Add(Build.A.BlockHeader.WithNumber(101).WithParent(parent).WithAura(1001, new byte[Signature.Size]).WithBeneficiary(TestItem.AddressA)
                    .WithExtraData([(byte)i]).WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(1000, 1001)).TestObject);
            }

            Parallel.ForEach(batch, header => _sealValidator.ValidateSeal(header, false));
            foreach (BlockHeader header in batch) _sealValidator.ValidateParams(parent, header);

            _ethereumEcdsa.ReceivedWithAnyArgs(batchSize).RecoverAddress(default, default(ValueHash256));
        }

        private static IEnumerable ValidateSealTests
        {
            get
            {
                yield return new TestCaseData(0UL, null, TestItem.AddressA).Returns(true).SetName("Genesis valid.").SetCategory("ValidSeal");
                yield return new TestCaseData(1UL, null, TestItem.AddressA).Returns(false).SetName("Wrong sealer.").SetCategory("ValidSeal");
                yield return new TestCaseData(1UL, null, null).Returns(true).SetName("General valid.").SetCategory("ValidSeal");
            }
        }

        [TestCaseSource(nameof(ValidateSealTests))]
        public bool validate_seal(ulong blockNumber, Address signedAddress, Address recoveredAddress)
        {
            signedAddress ??= _address;
            recoveredAddress ??= _address;

            BlockHeader block = Build.A.BlockHeader
                .WithAura(10, new byte[Signature.Size])
                .WithBeneficiary(_address)
                .WithNumber(blockNumber)
                .TestObject;

            ValueHash256 hash = block.CalculateValueHash(RlpBehaviors.ForSealing);
            bool signed = _wallet.TrySign(in hash, signedAddress, out Signature signature);
            Assert.That(signed, Is.True, "wallet should sign for unlocked test account");
            block.RequireAuRa().AuRaSignature = signature.BytesWithRecovery;
            _ethereumEcdsa.RecoverAddress(Arg.Any<Signature>(), in hash).Returns(recoveredAddress);

            return _sealValidator.ValidateSeal(block, false);
        }
    }
}
