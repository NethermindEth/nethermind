/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
using Nethermind.Core.Test;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
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
            
            _block = new Block(Prepare.A.BlockHeader().WithNumber(1).TestObject, new BlockBody());
            
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            
            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "getValidators"), Arg.Any<object[]>())
                .Returns(_getValidatorsData);
            
            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "finalizeChange"), Arg.Any<object[]>())
                .Returns(_finalizeChangeData);
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new ContractValidator(null, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_stateProvider()
        {
            Action act = () => new ContractValidator(_validator, null, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_abiEncoder()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, null, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_transactionProcessor()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, null, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, null, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_contractAddress()
        {
            _validator.Addresses = new Address[0];
            Action act = () => new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, 1);
            act.Should().Throw<ArgumentException>();
        }

        [Test]
        public void creates_system_account_on_start_block()
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
        public void loads_initial_validators_from_contract(InitializeValidatorsTestParameters test)
        {
            var initialValidators = Enumerable.Range(1, test.InitialValidatorsCount).Select(i => Address.FromNumber((UInt256) i)).ToArray();
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
                    StartBlockNumber = 1,
                    Reorganisations = new Dictionary<long, ConsecutiveInitiateChangeTestParameters.ChainInfo>()
                    {
                        {
                            1, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 1,
                                ExpectedFinalizationCount = 6,
                                NumberOfSteps = 30,
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
                            }
                        }
                    }
                })
                {
                    TestName = "consecutive_initiate_change_gets_finalized_and_switch_validators"
                };
                
                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    StartBlockNumber = 1,
                    Reorganisations = new Dictionary<long, ConsecutiveInitiateChangeTestParameters.ChainInfo>()
                    {
                        {
                            1, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 1,
                                ExpectedFinalizationCount = 4,
                                NumberOfSteps = 11,
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
                            }
                        }
                    }
                })
                {
                    TestName = "consecutive_initiate_change_gets_finalized_ignoring_duplicate_initiate_change"
                };
                
                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    StartBlockNumber = 1,
                    Reorganisations = new Dictionary<long, ConsecutiveInitiateChangeTestParameters.ChainInfo>()
                    {
                        {
                            1, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 1,
                                ExpectedFinalizationCount = 2,
                                NumberOfSteps = 11,
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
                                        // this will not get finalized because of reorganisation
                                    {
                                        Addresses = GenerateValidators(2),
                                        InitializeBlock = 5,
                                        FinalizeBlock = 8
                                    },
                                }
                            }
                        },
                        {
                            7, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 5, //reorganisation to block 5 in order to invalidate last initiate change 
                                ExpectedFinalizationCount = 0,
                                NumberOfSteps = 10,
                            }
                        }
                    }
                })
                {
                    TestName = "consecutive_initiate_change_reorganisation_ignores_reorganised_initiate_change"
                };
                
                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    StartBlockNumber = 1,
                    Reorganisations = new Dictionary<long, ConsecutiveInitiateChangeTestParameters.ChainInfo>()
                    {
                        {
                            1, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 1,
                                ExpectedFinalizationCount = 2,
                                NumberOfSteps = 11,
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
                                }
                            }
                        },
                        {
                            7, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 6,  
                                ExpectedFinalizationCount = 1,
                                NumberOfSteps = 10,
                                Validators = new  List<ConsecutiveInitiateChangeTestParameters.ValidatorsInfo>()
                                {
                                    new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                                    {
                                        Addresses = GenerateValidators(7),
                                        InitializeBlock = 8,
                                        FinalizeBlock = 11
                                    }
                                },
                            }
                        }
                    }
                })
                {
                    TestName = "consecutive_initiate_change_reorganisation_finalizes_after_reorganisation"
                };

                yield return new TestCaseData(new ConsecutiveInitiateChangeTestParameters
                {
                    StartBlockNumber = 1,
                    Reorganisations = new Dictionary<long, ConsecutiveInitiateChangeTestParameters.ChainInfo>()
                    {
                        {
                            1, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 1,
                                ExpectedFinalizationCount = 2,
                                NumberOfSteps = 11,
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
                                        // this will not be finalized even with reorganisation
                                        {
                                            Addresses = GenerateValidators(2),
                                            InitializeBlock = 5,
                                            FinalizeBlock = 8
                                        },
                                }
                            }
                        },
                        {
                            7, new ConsecutiveInitiateChangeTestParameters.ChainInfo()
                            {
                                BlockNumber = 6, //reorganisation to block 6 in order to keep last initiate change 
                                ExpectedFinalizationCount = 2,
                                NumberOfSteps = 10,
                                Validators = new List<ConsecutiveInitiateChangeTestParameters.ValidatorsInfo>()
                                {
                                    new ConsecutiveInitiateChangeTestParameters.ValidatorsInfo()
                                    {
                                        Addresses = GenerateValidators(7),
                                        InitializeBlock = 10,
                                        FinalizeBlock = 12
                                    }
                                },
                            }
                        }
                    }
                })
                {
                    TestName = "consecutive_initiate_change_reorganisation_finalizes_not_reorganised_initiate_change",
                };
            }
        }

        [TestCaseSource(nameof(ConsecutiveInitiateChangeData))]
        public void consecutive_initiate_change_gets_finalized_and_switch_validators(ConsecutiveInitiateChangeTestParameters test)
        {
            var currentValidators = GenerateValidators(1);
            SetupInitialValidators(currentValidators);
            
            var validator = new ContractValidator(_validator, _stateProvider, _abiEncoder, _transactionProcessor, _logManager, test.StartBlockNumber);
            test.TryDoReorganisations(test.StartBlockNumber, out _);
            for (int i = 0; i < test.Current.NumberOfSteps; i++)
            {
                var blockNumber = test.Current.BlockNumber + i;

                if (test.TryDoReorganisations(blockNumber, out var lastChain))
                {
                    ValidateFinalizationForChain(lastChain);
                    i = 0;
                    blockNumber = test.Current.BlockNumber + i;
                }
                
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
            
            ValidateFinalizationForChain(test.Current);
        }

        private void ValidateFinalizationForChain(ConsecutiveInitiateChangeTestParameters.ChainInfo chain)
        {
            // finalizeChange should be called or not based on test spec
            _transactionProcessor.Received(chain.ExpectedFinalizationCount)
                .Execute(Arg.Is<Transaction>(t => CheckTransaction(t, _finalizeChangeData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));
            
            _transactionProcessor.ClearReceivedCalls();
        }

        private static Address[] GenerateValidators(int number) => 
            Enumerable.Range(1, number).Select(i => Address.FromNumber((UInt256) i)).ToArray();

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
            private ChainInfo _last;
            
            public int StartBlockNumber { get; set; }

            public ChainInfo Current { get; set; } 
            
            public IDictionary<long, ChainInfo> Reorganisations { get; set; }
            
            public override string ToString() => JsonConvert.SerializeObject(this);

            public bool TryDoReorganisations(int blockNumber, out ChainInfo last)
            {
                if (Reorganisations.TryGetValue(blockNumber, out var chainInfo))
                {
                    _last = last = Current;
                    Current = chainInfo;
                    Reorganisations.Remove(blockNumber);
                    return true;
                }

                last = null;
                return false;
            }

            public TxReceipt[] GetReceipts(Block block, Address contractAddress, IAbiEncoder encoder, Func<Address[], byte[]> dataFunc)
            {
                var validators = Current.Validators?.FirstOrDefault(v => v.InitializeBlock == block.Number)?.Addresses;
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
                Current.Validators?.FirstOrDefault(v => v.FinalizeBlock > blockNumber)?.Addresses;

            public Address[] GetCurrentValidators(int blockNumber)
            {
                ValidatorsInfo LastFinalizedInitChange(ChainInfo chainInfo)
                {
                    return chainInfo.Validators?.LastOrDefault(v => v.FinalizeBlock <= blockNumber);
                }

                var lastFinalizedInitChange = LastFinalizedInitChange(Current) ?? LastFinalizedInitChange(_last);
                return lastFinalizedInitChange?.Addresses;
            }

            public class ValidatorsInfo
            {
                public Address[] Addresses { get; set; }

                public int InitializeBlock { get; set; }

                public int FinalizeBlock { get; set; }
            }

            public class ChainInfo
            {
                public int BlockNumber { get; set; }
                public int NumberOfSteps { get; set; }
                public int ExpectedFinalizationCount { get; set; }
                public IList<ValidatorsInfo> Validators { get; set; }               
            }
        }
    }
}