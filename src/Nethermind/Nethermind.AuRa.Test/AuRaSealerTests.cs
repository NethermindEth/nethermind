// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;

namespace Nethermind.AuRa.Test
{
    public class AuRaSealerTests
    {
        private AuRaSealer _auRaSealer;
        private IBlockTree _blockTree;
        private ulong _headStep;
        private IAuRaStepCalculator _auRaStepCalculator;
        private Address _address;
        private IValidatorStore _validatorStore;
        private IValidSealerStrategy _validSealerStrategy;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _headStep = 10UL;
            _blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithHash(Keccak.Compute("hash")).WithAura(_headStep, []).TestObject).TestObject);

            _auRaStepCalculator = Substitute.For<IAuRaStepCalculator>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _validSealerStrategy = Substitute.For<IValidSealerStrategy>();
            Signer signer = new(TestBlockchainIds.ChainId, Build.A.PrivateKey.TestObject, LimboLogs.Instance);
            _address = signer.Address;

            _auRaSealer = new AuRaSealer(
                _blockTree,
                _validatorStore,
                _auRaStepCalculator,
                signer,
                _validSealerStrategy,
                LimboLogs.Instance);
        }

        [TestCase(9UL, true, ExpectedResult = false, TestName = "Step too low-1.")]
        [TestCase(10UL, true, ExpectedResult = false, TestName = "Step too low-2.")]
        [TestCase(11UL, false, ExpectedResult = false, TestName = "Invalid sealer.")]
        [TestCase(11UL, true, ExpectedResult = true, TestName = "Can seal.")]
        public bool can_seal(ulong auRaStep, bool validSealer)
        {
            _auRaStepCalculator.CurrentStep.Returns(auRaStep);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), _address, auRaStep, out _).Returns(validSealer);
            return _auRaSealer.CanSeal(10, _blockTree.Head.Hash);
        }

        [Test]
        public async Task seal_can_recover_address()
        {
            _auRaStepCalculator.CurrentStep.Returns(11UL);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), _address, 11UL, out _).Returns(true);
            Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithBeneficiary(_address).WithAura(11UL, null).TestObject).TestObject;

            block = await _auRaSealer.SealBlock(block, CancellationToken.None);

            EthereumEcdsa ecdsa = new(BlockchainIds.Morden);
            Signature signature = new(block.Header.RequireAuRa().AuRaSignature);
            signature.V += Signature.VOffset;
            Address? recoveredAddress = ecdsa.RecoverAddress(signature, block.Header.CalculateHash(RlpBehaviors.ForSealing));

            Assert.That(recoveredAddress, Is.EqualTo(_address));
        }
    }
}
