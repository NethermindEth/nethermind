using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Test;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    [TestFixture]
    public class ValidatorContractTests
    {
        private Block _block;
        private readonly Address _contractAddress = Address.FromNumber(long.MaxValue);

        [SetUp]
        public void SetUp()
        {
            _block = new Block(Prepare.A.BlockHeader().TestObject, new BlockBody());
        }

        [Test]
        public void constructor_throws_ArgumentNullException_on_null_encoder()
        {
            Action action = () => new ValidatorContract(null, _contractAddress);
            action.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_throws_ArgumentNullException_on_null_contractAddress()
        {
            Action action = () => new ValidatorContract(new AbiEncoder(), null);
            action.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void finalize_change_should_return_valid_transaction_when_validator_available()
        {
            var expectation = new Transaction()
            {
                Value = 0, 
                Data = new byte[] {0x75, 0x28, 0x62, 0x11},
                Hash = new Keccak("0xa43db3bf833748d98eb99453bc933f313f9f6a7471fed0018190f0d5b0f863a1"), 
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = long.MaxValue,
                GasPrice = 0,
                Nonce = 0
            };
            
            var contract = new ValidatorContract(new AbiEncoder(), _contractAddress);
            
            var transaction = contract.FinalizeChange();
            
            transaction.Should().BeEquivalentTo(expectation);
        }
    }
}