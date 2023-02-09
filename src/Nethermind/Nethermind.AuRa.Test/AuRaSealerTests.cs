// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using NUnit.Framework;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Wallet;
using NSubstitute;

namespace Nethermind.AuRa.Test
{
    public class AuRaSealerTests
    {
        private AuRaSealer _auRaSealer;
        private IBlockTree _blockTree;
        private int _headStep;
        private IAuRaStepCalculator _auRaStepCalculator;
        private Address _address;
        private IValidatorStore _validatorStore;
        private IValidSealerStrategy _validSealerStrategy;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _headStep = 10;
            _blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithHash(Keccak.Compute("hash")).WithAura(_headStep, Array.Empty<byte>()).TestObject).TestObject);

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

        [TestCase(9, true, ExpectedResult = false, TestName = "Step too low-1.")]
        [TestCase(10, true, ExpectedResult = false, TestName = "Step too low-2.")]
        [TestCase(11, false, ExpectedResult = false, TestName = "Invalid sealer.")]
        [TestCase(11, true, ExpectedResult = true, TestName = "Can seal.")]
        public bool can_seal(long auRaStep, bool validSealer)
        {
            _auRaStepCalculator.CurrentStep.Returns(auRaStep);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), _address, auRaStep).Returns(validSealer);
            return _auRaSealer.CanSeal(10, _blockTree.Head.Hash);
        }

        [Test]
        public async Task seal_can_recover_address()
        {
            _auRaStepCalculator.CurrentStep.Returns(11);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), _address, 11).Returns(true);
            Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithBeneficiary(_address).WithAura(11, null).TestObject).TestObject;

            block = await _auRaSealer.SealBlock(block, CancellationToken.None);

            EthereumEcdsa ecdsa = new(BlockchainIds.Morden, LimboLogs.Instance);
            Signature signature = new(block.Header.AuRaSignature);
            signature.V += Signature.VOffset;
            Address? recoveredAddress = ecdsa.RecoverAddress(signature, block.Header.CalculateHash(RlpBehaviors.ForSealing));

            recoveredAddress.Should().Be(_address);
        }
    }
}
