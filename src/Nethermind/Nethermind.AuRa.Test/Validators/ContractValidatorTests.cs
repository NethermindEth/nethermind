using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
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
            Action act = () => new ContractValidator(null, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_stateProvider()
        {
            Action act = () => new ContractValidator(_validator, null, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_abiEncoder()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, null, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void Throws_ArgumentNullException_on_empty_transactionProcessor()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, null, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void Throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, null, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void Throws_ArgumentNullException_on_empty_contractAddress()
        {
            _validator.Addresses = new Address[0];
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentException>();
        }

        [Test]
        public void Creates_system_account_on_start_block()
        {
            SetupInitialValidators(Address.FromNumber(2000));
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, 1);
            
            validator.PreProcess(_block);
            
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
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, test.StartBlockNumber);

            for (int i = 0; i < test.NumberOfSteps; i++)
            {
                _block.Number = test.BlockNumber + i;
                _block.Beneficiary = initialValidators[i % initialValidators.Length];
                validator.PreProcess(_block);
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

        public static IEnumerable<TestCaseData> ConsecutiveInitiateChangeData
        {
            get
            {
                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    BlockNumber = 1,
                    StartBlockNumber = 1,
                    NumberOfSteps = 30,
                    ExpectedFinalizationCount = 6,
                    Validators = new List<ConsecutiveInitiateChangeTestParameters.ValidatorsInfo>()
                    {
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(1),
                            InitializeBlock = 0,
                            FinalizeBlock = 1
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(2),
                            InitializeBlock = 3,
                            FinalizeBlock = 4
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(3),
                            InitializeBlock = 6,
                            FinalizeBlock = 8
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(4),
                            InitializeBlock = 10,
                            FinalizeBlock = 12
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(10),
                            InitializeBlock = 15,
                            FinalizeBlock = 18
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(5),
                            InitializeBlock = 20,
                            FinalizeBlock = 26
                        },
                    }
                })
                {
                    TestName = "Consecutive_InitiateChange_gets_finalized_and_switch_validators"
                };
                
                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    BlockNumber = 1,
                    StartBlockNumber = 1,
                    NumberOfSteps = 11,
                    ExpectedFinalizationCount = 4,
                    Validators = new List<ConsecutiveInitiateChangeTestParameters.ValidatorsInfo>()
                    {
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(1),
                            InitializeBlock = 0,
                            FinalizeBlock = 1
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(5),
                            InitializeBlock = 3,
                            FinalizeBlock = 4
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(2),
                            InitializeBlock = 5,
                            FinalizeBlock = 8
                        },
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(20),
                            InitializeBlock = 7,
                            FinalizeBlock = Int32.MaxValue // IgnoredInitializeChange
                        },                        
                        new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                        {
                            Addresses = GenerateValidators(3),
                            InitializeBlock = 9,
                            FinalizeBlock = 11
                        },
                    }
                })
                {
                    TestName = "Consecutive_InitiateChange_gets_finalized_ignoring_duplicate_InitiateChange"
                };
            }
        }

        [TestCaseSource(nameof(ConsecutiveInitiateChangeData))]
        public void Consecutive_InitiateChange_gets_finalized_and_switch_validators(ConsecutiveInitiateChangeTestParameters test)
        {
            var currentValidators = GenerateValidators(1);
            SetupInitialValidators(currentValidators);
            
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, test.StartBlockNumber);
            
            for (int i = 0; i < test.NumberOfSteps; i++)
            {
                var blockNumber = test.BlockNumber + i;
                _block.Number = blockNumber;
                _block.Beneficiary = currentValidators[i % currentValidators.Length];
                var txReceipts = test.GetReceipts(_block, _contractAddress, _abiEncoder, SetupAbiAddresses);
                _block.Bloom = new Bloom(txReceipts.SelectMany(r => r.Logs).ToArray());
                
                validator.PreProcess(_block);
                validator.PostProcess(_block, txReceipts);
                
                currentValidators = test.GetCurrentValidators(blockNumber);
                var nextValidators = test.GetNextValidators(blockNumber);
                currentValidators.Select(a => validator.IsValidSealer(a)).Should().AllBeEquivalentTo(true, $"Validator address is not recognized in block {blockNumber}");
                nextValidators?.Except(currentValidators).Select(a => validator.IsValidSealer(a)).Should().AllBeEquivalentTo(false);
            }
            
            // finalizeChange should be called or not based on test spec
            _transactionProcessor.Received(test.ExpectedFinalizationCount)
                .Execute(Arg.Is<Transaction>(t => CheckTransaction(t, _finalizeChangeData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));
        }

        private static Address[] GenerateValidators(int number) => 
            Enumerable.Range(1, number).Select(i => Address.FromNumber(i)).ToArray();

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
        
        public class ConsecutiveInitiateChangeTestParameters
        {
            public int StartBlockNumber { get; set; }
            public int BlockNumber { get; set; }
            public int NumberOfSteps { get; set; }
            public int ExpectedFinalizationCount { get; set; }

            public IList<ValidatorsInfo> Validators { get; set; }

            public override string ToString() => JsonConvert.SerializeObject(this);

            public TxReceipt[] GetReceipts(Block block, Address contractAddress, IAbiEncoder encoder, Func<Address[], byte[]> dataFunc)
            {
                var validators = Validators.FirstOrDefault(v => v.InitializeBlock == block.Number)?.Addresses;
                if (validators == null)
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    var logs = new[]
                    {
                        new LogEntry(contractAddress,
                            dataFunc(validators),
                            new[] {ValidatorContract.Definition.initiateChangeEventHash, block.ParentHash})
                    };
                    
                    return new TxReceipt[]
                    {
                        new TxReceipt()
                        {
                            Logs = logs,
                            Bloom = new Bloom(logs)
                        }
                    };
                }
            }

            public Address[] GetNextValidators(int blockNumber) => 
                Validators.FirstOrDefault(v => v.FinalizeBlock > blockNumber)?.Addresses;

            public Address[] GetCurrentValidators(int blockNumber) => 
                Validators.LastOrDefault(v => v.FinalizeBlock <= blockNumber)?.Addresses;

            public class ValidatorsInfo
            {
                public Address[] Addresses { get; set; }

                public int InitializeBlock { get; set; }

                public int FinalizeBlock { get; set; }
            }
        }
    }
}