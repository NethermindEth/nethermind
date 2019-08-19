using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization.Formatters;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ListValidatorTests
    {
        private const string Include1 = "0xffffffffffffffffffffffffffffffffffffffff";
        private const string Include2 = "0xfffffffffffffffffffffffffffffffffffffffe";
        
        [TestCase(Include1, ExpectedResult = true)]
        [TestCase(Include2, ExpectedResult = true)]
        [TestCase("0xAAfffffffffffffffffffffffffffffffffffffe", ExpectedResult = false)]
        [TestCase("0xfffffffffffffffffffffffffffffffffffffffd", ExpectedResult = false)]
        public bool Should_validate_correctly(string address)
        {
            var validator = new ListValidator(
                new AuRaParameters.Validator()
                {
                    Addresses = new[] {new Address(Include1), new Address(Include2), }
                });

            return validator.IsValidSealer(new Address(address));
        }

        [Test]
        public void Throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new ListValidator(null);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentException_on_wrong_validator_type()
        {
            Action act = () => new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.Contract,
                    Addresses = new[] {Address.Zero}
                });
            
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void Throws_ArgumentException_on_empty_addresses()
        {
            Action act = () => new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.List
                });
            
            act.Should().Throw<ArgumentException>();
        }
    }

    public class ContractValidatorTests
    {
        private IStateProvider _stateProvider;
        private IAbiEncoder _abiEncoder;
        private ILogManager _logManager;
        private AuRaParameters.Validator _validator;
        private Block _block;
        private ITransactionProcessor _transactionProcessor;
        private Address _contractAddress = Address.FromNumber(1000);
        private byte[] _getValidatorsData = new byte[] {0, 1, 2};
        private byte[] _finalizeChangeData= new byte[] {3, 4, 5};
        private Address[] _initialValidators;

        [SetUp]
        public void SetUp()
        {
            _stateProvider = Substitute.For<IStateProvider>();
            _abiEncoder = Substitute.For<IAbiEncoder>();
            _logManager = Substitute.For<ILogManager>();
            _validator = new AuRaParameters.Validator()
            {
                Addresses = new[] {_contractAddress},
                ValidatorType = AuRaParameters.ValidatorType.Contract
            };
            
            _block = new Block(
                new BlockHeader(
                    Keccak.Zero,
                    Keccak.Zero,
                    Address.Zero,
                    UInt256.One,
                    1,
                    0,
                    UInt256.One,
                    Array.Empty<byte>()
                ), new BlockBody());
            
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            
            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "getValidators"), Arg.Any<object[]>())
                .Returns(_getValidatorsData);
            
            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "finalizeChange"), Arg.Any<object[]>())
                .Returns(_finalizeChangeData);
        }

        [Test]
        public void Throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new ContractValidator(null, _stateProvider, _abiEncoder, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_stateProvider()
        {
            Action act = () => new ContractValidator(_validator, null, _abiEncoder, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_abiEncoder()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, null, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, null, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_contractAddress()
        {
            _validator.Addresses = new Address[0];
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, _logManager, 1);
            act.Should().Throw<ArgumentException>();
        }

        [Test]
        public void Creates_system_account_on_start_block()
        {
            SetupInitialValidators(Address.FromNumber(2000));
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _logManager, 1);
            
            validator.PreProcess(_block, _transactionProcessor);
            
            _stateProvider.Received(1).CreateAccount(Address.SystemUser, UInt256.Zero);
            _stateProvider.Received(1).Commit(Homestead.Instance);
        }

        public static IEnumerable<InitializeValidatorsTestParameters> LoadsInitialValidatorsFromContractData
        {
            get
            {
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 1,
                    NumberOfSteps = 1,
                    ExpectedFinalizationCount = 1
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 100,
                    InitialValidatorsCount = 1,
                    NumberOfSteps = 2,
                    ExpectedFinalizationCount = 0
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 100,
                    BlockNumber = 100,
                    InitialValidatorsCount = 1,
                    NumberOfSteps = 1,
                    ExpectedFinalizationCount = 1
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 2,
                    NumberOfSteps = 2,
                    ExpectedFinalizationCount = 0
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 2,
                    NumberOfSteps = 3,
                    ExpectedFinalizationCount = 1
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 3,
                    NumberOfSteps = 2,
                    ExpectedFinalizationCount = 0
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 3,
                    NumberOfSteps = 3,
                    ExpectedFinalizationCount = 1
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 10,
                    NumberOfSteps = 5,
                    ExpectedFinalizationCount = 0
                };
                
                yield return new InitializeValidatorsTestParameters()
                {
                    StartBlockNumber = 1,
                    BlockNumber = 1,
                    InitialValidatorsCount = 10,
                    NumberOfSteps = 7,
                    ExpectedFinalizationCount = 1
                };
            }
        }
            
        [TestCaseSource(nameof(LoadsInitialValidatorsFromContractData))]
        public void Loads_initialValidators_from_contract(InitializeValidatorsTestParameters test)
        {
            var initialValidators = Enumerable.Range(1, test.InitialValidatorsCount).Select(i => Address.FromNumber(i)).ToArray();
            SetupInitialValidators(initialValidators);
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _logManager, test.StartBlockNumber);

            for (int i = 0; i < test.NumberOfSteps; i++)
            {
                _block.Number = test.BlockNumber + i;
                _block.Beneficiary = initialValidators[i % initialValidators.Length];
                validator.PreProcess(_block, _transactionProcessor);
            }

            // getValidators should have been called
            _transactionProcessor.Received(1)
                .Execute(
                    Arg.Is<Transaction>(t => CheckTransaction(t, _getValidatorsData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));

            // finalizeChange should be called or not based on test spec
            _transactionProcessor.Received(test.ExpectedFinalizationCount)
                .Execute(Arg.Is<Transaction>(t => CheckTransaction(t, _finalizeChangeData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));
            
            // all initial validators should be true
            initialValidators.Select(a => validator.IsValidSealer(a)).Should().AllBeEquivalentTo(true);
        }

        public void Consecutive_InitializeChange_gets_finalized_and_switch_validators(ConsecutiveInitializeChangeTestParameters test)
        {
            var currentValidators = new[] {Address.FromNumber(1)};
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _logManager, test.StartBlockNumber);
            
            for (int i = 0; i < test.NumberOfSteps; i++)
            {
                var blockNumber = test.BlockNumber + i;
                _block.Number = blockNumber;
                _block.Beneficiary = currentValidators[i % currentValidators.Length];
                var txReceipts = test.GetReceipts(blockNumber);
                _block.Bloom = new Bloom(txReceipts.SelectMany(r => r.Logs).ToArray());
                currentValidators = test.GetCurrentValidators(blockNumber);
                
                validator.PreProcess(_block, _transactionProcessor);
                validator.PostProcess(_block, txReceipts, _transactionProcessor);
                
                currentValidators.Select(a => validator.IsValidSealer(a)).Should().AllBeEquivalentTo(true);
                test.GetNextValidators(blockNumber)?.Select(a => validator.IsValidSealer(a)).Should().Contain(false);
            }
            
            // finalizeChange should be called or not based on test spec
            _transactionProcessor.Received(test.ExpectedFinalizationCount)
                .Execute(Arg.Is<Transaction>(t => CheckTransaction(t, _finalizeChangeData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));
        }
        
        private void SetupInitialValidators(params Address[] initialValidators)
        {
            _initialValidators = initialValidators;
            _transactionProcessor.When(x => x.Execute(
                    Arg.Is<Transaction>(t => CheckTransaction(t, _getValidatorsData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer)))
                .Do(args =>
                    args.Arg<ITxTracer>().MarkAsSuccess(
                        args.Arg<Transaction>().To,
                        0,
                        SetupAbiAddresses(_initialValidators),
                        Array.Empty<LogEntry>()));
        }

        private byte[] SetupAbiAddresses(Address[] addresses)
        {
            byte[] data = addresses.SelectMany(a => a.Bytes).ToArray();

            _abiEncoder.Decode(
                AbiEncodingStyle.None,
                Arg.Is<AbiSignature>(s => s.Types.Length == 1 && s.Types[0].CSharpType == typeof(Address[])),
                data).Returns(new object[] {addresses});

            return data;
        }
        
        private bool CheckTransaction(Transaction t, byte[] transactionData)
        {
            return t.SenderAddress == Address.SystemUser && t.To == _contractAddress && t.Data == transactionData;
        }
        
        public class InitializeValidatorsTestParameters
        {
            public int StartBlockNumber { get; set; }
            public int BlockNumber { get; set; }
            public int InitialValidatorsCount { get; set; }
            public int NumberOfSteps { get; set; }
            public int ExpectedFinalizationCount { get; set; }

            public override string ToString() => JsonConvert.SerializeObject(this);
        }
        
        public class ConsecutiveInitializeChangeTestParameters
        {
            public int StartBlockNumber { get; set; }
            public int BlockNumber { get; set; }
            public int NumberOfSteps { get; set; }
            public int ExpectedFinalizationCount { get; set; }

            public IList<ValidatorsInfo> Validators { get; set; }

            public override string ToString() => JsonConvert.SerializeObject(this);

            public TxReceipt[] GetReceipts(int blockNumber)
            {
                var validators = Validators.FirstOrDefault(v => v.InitializeBlock == blockNumber)?.Addresses;
                if (validators == null)
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    return new TxReceipt[] {new TxReceipt()
                    {
                        // Logs = new []{ new LogEntry(), }
                    }};
                }
            }

            public Address[] GetNextValidators(int blockNumber) => 
                Validators.FirstOrDefault(v => v.FinalizeBlock > blockNumber)?.Addresses;

            public Address[] GetCurrentValidators(int blockNumber) => 
                Validators.LastOrDefault(v => v.FinalizeBlock < blockNumber)?.Addresses;

            public class ValidatorsInfo
            {
                public Address[] Addresses { get; set; }

                public int InitializeBlock { get; set; }

                public int FinalizeBlock { get; set; }
            }
        }
    }
}