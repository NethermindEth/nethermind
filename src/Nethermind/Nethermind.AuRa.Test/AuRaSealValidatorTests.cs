//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Security;
using FluentAssertions;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
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
        private AuRaParameters _auRaParameters;
        private IAuRaStepCalculator _auRaStepCalculator;
        private ILogManager _logManager;
        private IWallet _wallet;
        private Address _address;
        private IEthereumEcdsa _ethereumEcdsa;
        private static int _currentStep;

        [SetUp]
        public void SetUp()
        {
            _auRaParameters = new AuRaParameters();
            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _logManager = NullLogManager.Instance;
            _wallet = new DevWallet(new WalletConfig(), _logManager);
            _address = _wallet.NewAccount(new NetworkCredential(string.Empty, "AAA").SecurePassword);
            
            _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
            _currentStep = 11;
            _auRaStepCalculator.CurrentStep.Returns(_currentStep);
            
            _sealValidator = new AuRaSealValidator(_auRaParameters, 
                _auRaStepCalculator,
                Substitute.For<IValidatorStore>(),
                _ethereumEcdsa, 
                _logManager);
        }
        
        private static IEnumerable ValidateParamsTests
        {
            get
            {
                long step = 10;
                long parentStep = 9;
                
                BlockHeaderBuilder GetBlock() => Build.A.BlockHeader
                        .WithAura(10, Bytes.Empty)
                        .WithBeneficiary(TestItem.AddressA)
                        .WithDifficulty(AuraDifficultyCalculator.CalculateDifficulty(parentStep, step));

                BlockHeaderBuilder GetParentBlock() => Build.A.BlockHeader
                    .WithAura(parentStep, Bytes.Empty)
                    .WithBeneficiary(TestItem.AddressB);

                TestCaseData GetTestCaseData(BlockHeaderBuilder parent, BlockHeaderBuilder block, Action<AuRaParameters> paramAction = null) =>
                    new TestCaseData(parent.TestObject, block.TestObject, paramAction);
                

                yield return GetTestCaseData(GetParentBlock(), GetBlock())
                    .Returns(true).SetName("General valid.");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(step, null))
                    .Returns(false).SetName("Missing AuRaSignature").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep, Bytes.Empty))
                    .Returns(false).SetName("Duplicate block.").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep - 1, Bytes.Empty))
                    .Returns(false).SetName("Past block.").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(_currentStep + 5, Bytes.Empty))
                    .Returns(false).SetName("Future block.").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(AuraDifficultyCalculator.MaxDifficulty))
                    .Returns(false).SetName("Difficulty too large.").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000))
                    .Returns(false).SetName("Wrong difficulty.").SetCategory("ValidParams");

                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithDifficulty(1000), a => a.ValidateScoreTransition = 100)
                    .Returns(true).SetName("Skip difficulty validation due to ValidateScoreTransition.").SetCategory("ValidParams");
                
                yield return GetTestCaseData(GetParentBlock(), GetBlock().WithAura(parentStep - 1, Bytes.Empty), a => a.ValidateScoreTransition = a.ValidateStepTransition = 100)
                    .Returns(true).SetName("Skip step validation due to ValidateStepTransition.").SetCategory("ValidParams");
            }
        }

        [TestCaseSource(nameof(ValidateParamsTests))]
        public bool validate_params(BlockHeader parentBlock, BlockHeader block, Action<AuRaParameters> modifyParameters)
        {
            modifyParameters?.Invoke(_auRaParameters);
            return _sealValidator.ValidateParams(parentBlock, block);
        }

        private static IEnumerable ValidateSealTests
        {
            get
            {
                yield return new TestCaseData(0, null, TestItem.AddressA).Returns(true).SetName("Genesis valid.").SetCategory("ValidSeal");;
                yield return new TestCaseData(1, null, TestItem.AddressA).Returns(false).SetName("Wrong sealer.").SetCategory("ValidSeal");;
                yield return new TestCaseData(1, null, null).Returns(true).SetName("General valid.").SetCategory("ValidSeal");
            }
        }
        
        [TestCaseSource(nameof(ValidateSealTests))]
        public bool validate_seal(long blockNumber, Address signedAddress, Address recoveredAddress)
        {
            signedAddress ??= _address;
            recoveredAddress ??= _address;
            
            var block = Build.A.BlockHeader
                .WithAura(10, Bytes.Empty)
                .WithBeneficiary(_address)
                .WithNumber(blockNumber)
                .TestObject;
            
            var hash = block.CalculateHash(RlpBehaviors.ForSealing);
            block.AuRaSignature = _wallet.Sign(hash, signedAddress).BytesWithRecovery;
            _ethereumEcdsa.RecoverAddress(Arg.Any<Signature>(), hash).Returns(recoveredAddress);

            return _sealValidator.ValidateSeal(block, false);
        }
    }
}