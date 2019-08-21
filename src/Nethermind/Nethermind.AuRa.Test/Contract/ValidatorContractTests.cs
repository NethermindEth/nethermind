using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NSubstitute;
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
            _block = new Block(
                new BlockHeader(
                    Keccak.Zero,
                    Keccak.Zero,
                    Address.Zero,
                    UInt256.One,
                    1,
                    1,
                    UInt256.One
                    , new byte[0]),
                new BlockBody());
        }

        [TestCase(1, null, Description = "No validators.")]
        [TestCase(10, 11, Description = "Validators after current block.")]
        public void finalize_change_should_return_empty_transaction_when_no_contract_address(int blockNumber, int? validatorAt)
        {
            _block.Number = blockNumber;

            var contract = new ValidatorContract(new AbiEncoder(), _contractAddress);
            
            var transaction = contract.FinalizeChange();
            
            transaction.Should().BeNull();
        }
        
        [Test]
        public void finalize_change_should_return_valid_transaction_when_validator_available()
        {
            var expectation = new Transaction()
            {
                Value = 0, 
                Data = new byte[] {0x75, 0x28, 0x62, 0x11},
                Hash = new Keccak("0x8a6718cf7896946b1a5e721f9f61f03f3a285e00f9a5341649fbfeb44b9dc2da"), 
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = 1,
                GasPrice = 0,
                Nonce = 0
            };
            
            var contract = new ValidatorContract(new AbiEncoder(), _contractAddress);
            
            var transaction = contract.FinalizeChange();
            
            transaction.Should().BeEquivalentTo(expectation);
        }
    }
}