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
        private IStateProvider _stateProvider;

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
            
            _stateProvider = Substitute.For<IStateProvider>();
        }

        private static AuRaParameters GetAuRaParameters(params long[] blockNumbers) => new AuRaParameters()
            {
                Validators = blockNumbers.ToDictionary(b => b, b => Address.FromNumber(b))
            };

        [TestCase(1, null, Description = "No validators.")]
        [TestCase(10, 11, Description = "Validators after current block.")]
        public void finalize_change_should_return_empty_transaction_when_no_validator_available_for_block(int blockNumber, int? validatorAt)
        {
            _block.Number = blockNumber;

            var auRaParameters = GetAuRaParameters(validatorAt.HasValue ? new long[] {validatorAt.Value} : new long[0]);
            var contract = new ValidatorContract(new AbiEncoder(), auRaParameters);
            
            var transaction = contract.FinalizeChange(_block, _stateProvider);
            
            transaction.Should().BeNull();
        }
        
        [Test]
        public void finalize_change_should_return_valid_transaction_when_validator_available()
        {
            var auRaParameters = GetAuRaParameters(0);
            
            var expectation = new Transaction()
            {
                Value = 0, 
                Data = new byte[] {0x75, 0x28, 0x62, 0x11},
                Hash = new Keccak("0x69f34df2b26dbd72c18eefc0d1426ee1cbe53ab2765bfc76dd645e794d56454c"), 
                To = auRaParameters.Validators.First().Value,
                SenderAddress = Address.SystemUser,
                GasLimit = 1,
                GasPrice = 0,
                Nonce = 0
            };
            
            var contract = new ValidatorContract(new AbiEncoder(), auRaParameters);
            
            var transaction = contract.FinalizeChange(_block, _stateProvider);
            
            transaction.Should().BeEquivalentTo(expectation);
        }
        
        [TestCase(40, new long[] {0, 12, 25, 36})]
        public void finalize_change_should_return_transactions_with_valid_addresses_on_subsequent_calls(int blocksCount, long[] blockNumbersValidators)
        {
            var auRaParameters = GetAuRaParameters(blockNumbersValidators);
            var contract = new ValidatorContract(new AbiEncoder(), auRaParameters);
            
            var blocks = Enumerable.Range(1, blocksCount).ToArray();
            
            var result = blocks
                .Select(blockNumber =>
                {
                    _block.Number = blockNumber;
                    var transaction = contract.FinalizeChange(_block, _stateProvider);
                    return transaction.To;
                });

            var expectation = blocks.Select(blockNumber => Address.FromNumber(blockNumbersValidators.Where(n => n <= blockNumber).Max()));

            result.Should().BeEquivalentTo(expectation);
        }
    }
}