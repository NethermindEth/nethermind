// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Core.Specs;
using System.Text.Json;

namespace Nethermind.AuRa.Test.Validators
{
    public class ContractBasedValidatorTests
    {
        private IWorldState _stateProvider;
        private IAbiEncoder _abiEncoder;
        private ILogManager _logManager;
        private AuRaParameters.Validator _validator;
        private Block _block;
        private BlockHeader _parentHeader;
        private IReadOnlyTransactionProcessor _transactionProcessor;
        private IAuRaBlockFinalizationManager _blockFinalizationManager;
        private static Address _contractAddress = Address.FromNumber(1000);
        private (Address Sender, byte[] TransactionData) _getValidatorsData = (Address.Zero, new byte[] { 0, 1, 2 });
        private (Address Sender, byte[] TransactionData) _finalizeChangeData = (Address.SystemUser, new byte[] { 3, 4, 5 });
        private Address[] _initialValidators;
        private IBlockTree _blockTree;
        private IReceiptStorage _receiptsStorage;
        private IValidatorStore _validatorStore;
        private IValidSealerStrategy _validSealerStrategy;
        private IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
        private ValidatorContract _validatorContract;

        [SetUp]
        public void SetUp()
        {
            _validatorStore = new ValidatorStore(new MemDb());
            _validSealerStrategy = new ValidSealerStrategy();
            _stateProvider = Substitute.For<IWorldState>();
            _abiEncoder = Substitute.For<IAbiEncoder>();
            _logManager = LimboLogs.Instance;
            _blockTree = Substitute.For<IBlockTree>();
            _blockFinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
            _receiptsStorage = Substitute.For<IReceiptStorage>();
            _validator = new AuRaParameters.Validator()
            {
                Addresses = new[] { _contractAddress },
                ValidatorType = AuRaParameters.ValidatorType.Contract
            };
            _block = new Block(Build.A.BlockHeader.WithNumber(1).WithAura(1, Array.Empty<byte>()).TestObject, new BlockBody());

            _transactionProcessor = Substitute.For<IReadOnlyTransactionProcessor>();
            _transactionProcessor.IsContractDeployed(_contractAddress).Returns(true);
            _readOnlyTxProcessorSource = Substitute.For<IReadOnlyTxProcessorSource>();
            _readOnlyTxProcessorSource.Build(Arg.Any<Keccak>()).Returns(_transactionProcessor);
            _stateProvider.StateRoot.Returns(TestItem.KeccakA);
            _blockTree.Head.Returns(_block);

            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "getValidators"), Arg.Any<object[]>())
                .Returns(_getValidatorsData.TransactionData);

            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "finalizeChange"), Arg.Any<object[]>())
                .Returns(_finalizeChangeData.TransactionData);

            _validatorContract = new ValidatorContract(_transactionProcessor, _abiEncoder, _contractAddress, _stateProvider, _readOnlyTxProcessorSource, new Signer(0, TestItem.PrivateKeyD, LimboLogs.Instance));
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_validatorStore()
        {
            Action act = () => new ContractBasedValidator(_validatorContract, _blockTree, _receiptsStorage, null, _validSealerStrategy, _blockFinalizationManager, default, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_validSealearStrategy()
        {
            Action act = () => new ContractBasedValidator(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, null, _blockFinalizationManager, default, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_blockTree()
        {
            Action act = () => new ContractBasedValidator(_validatorContract, null, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, default, _logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new ContractBasedValidator(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, default, null, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void creates_system_account_on_start_block()
        {
            Address initialValidator = Address.FromNumber(2000);
            SetupInitialValidators(initialValidator);
            _block.Header.Beneficiary = initialValidator;
            ContractBasedValidator validator = new(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, default, _logManager, 1);

            validator.OnBlockProcessingStart(_block);

            _stateProvider.Received(1).CreateAccount(Address.SystemUser, UInt256.Zero);
            _stateProvider.Received(1).Commit(Homestead.Instance);
        }

        [Test]
        public void initializes_pendingValidators_from_db()
        {
            _validatorStore = Substitute.For<IValidatorStore>();

            int blockNumber = 10;
            Address[] validators = TestItem.Addresses.Take(10).ToArray();
            Keccak blockHash = Keccak.Compute("Test");
            PendingValidators pendingValidators = new(blockNumber, blockHash, validators);
            _validatorStore.PendingValidators.Returns(pendingValidators);
            _blockTree.Head.Returns((Block)null);

            IAuRaValidator validator = new ContractBasedValidator(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, default, _logManager, 1);

            _blockFinalizationManager.BlocksFinalized +=
                Raise.EventWith(new FinalizeEventArgs(_block.Header,
                    Build.A.BlockHeader.WithNumber(blockNumber).WithHash(blockHash).TestObject));

            validator.Validators.Should().BeEquivalentTo(validators, o => o.WithStrictOrdering());
        }

        [TestCase(1)]
        [TestCase(10)]
        public void loads_initial_validators_from_contract(long blockNumber)
        {
            Address initialValidator = TestItem.AddressA;
            Block block = Build.A.Block.WithParent(_parentHeader).WithNumber(blockNumber).WithBeneficiary(initialValidator).WithAura(1, Array.Empty<byte>()).TestObject;
            SetupInitialValidators(block.Header, initialValidator);
            int startBlockNumber = 1;
            ContractBasedValidator validator = new(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, _parentHeader, _logManager, startBlockNumber);

            bool finalizeChangeCalled = blockNumber == 1;

            if (!finalizeChangeCalled)
            {
                validator.Validators = new[] { TestItem.AddressD };
            }

            validator.OnBlockProcessingStart(block);

            // getValidators should have been called
            _transactionProcessor.Received()
                .CallAndRestore(
                    Arg.Is<Transaction>(t => CheckTransaction(t, _getValidatorsData)),
                    _parentHeader,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));

            // finalizeChange should be called
            _transactionProcessor.Received(finalizeChangeCalled ? 1 : 0)
                .Execute(Arg.Is<Transaction>(t => CheckTransaction(t, _finalizeChangeData)),
                    block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer));

            // initial validator should be true
            Address[] expectedValidators = { initialValidator };
            validator.Validators.Should().BeEquivalentTo(expectedValidators, o => o.WithStrictOrdering());
            _validatorStore.GetValidators().Should().BeEquivalentTo(expectedValidators.AsEnumerable());
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(1),
                                        InitializeBlock = 0,
                                        FinalizeBlock = 0
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(2),
                                        InitializeBlock = 3,
                                        FinalizeBlock = 3
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(3),
                                        InitializeBlock = 6,
                                        FinalizeBlock = 7
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(4),
                                        InitializeBlock = 10,
                                        FinalizeBlock = 11
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(10),
                                        InitializeBlock = 15,
                                        FinalizeBlock = 17
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(5),
                                        InitializeBlock = 20,
                                        FinalizeBlock = 25
                                    },
                                }
                            }
                        }
                    },
                    TestName = "consecutive_initiate_change_gets_finalized_and_switch_validators"
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(1),
                                        InitializeBlock = 0,
                                        FinalizeBlock = 0
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(5),
                                        InitializeBlock = 3,
                                        FinalizeBlock = 3
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(2),
                                        InitializeBlock = 5,
                                        FinalizeBlock = 7
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(20),
                                        InitializeBlock = 7,
                                        FinalizeBlock = Int32.MaxValue // IgnoredInitializeChange
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(3),
                                        InitializeBlock = 9,
                                        FinalizeBlock = 10
                                    },
                                }
                            }
                        }
                    },
                    TestName = "consecutive_initiate_change_gets_finalized_ignoring_duplicate_initiate_change"
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(1),
                                        InitializeBlock = 0,
                                        FinalizeBlock = 0
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(5),
                                        InitializeBlock = 3,
                                        FinalizeBlock = 3
                                    },
                                    new()
                                        // this will not get finalized because of reorganisation
                                    {
                                        Addresses = GenerateValidators(2),
                                        InitializeBlock = 5,
                                        FinalizeBlock = 7
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
                    },
                    TestName = "consecutive_initiate_change_reorganisation_ignores_reorganised_initiate_change"
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(1),
                                        InitializeBlock = 0,
                                        FinalizeBlock = 0
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(5),
                                        InitializeBlock = 3,
                                        FinalizeBlock = 3
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(7),
                                        InitializeBlock = 8,
                                        FinalizeBlock = 10
                                    }
                                },
                            }
                        }
                    },
                    TestName = "consecutive_initiate_change_reorganisation_finalizes_after_reorganisation"
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(1),
                                        InitializeBlock = 0,
                                        FinalizeBlock = 0
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(5),
                                        InitializeBlock = 3,
                                        FinalizeBlock = 3
                                    },
                                    new()
                                    {
                                        Addresses = GenerateValidators(2),
                                        InitializeBlock = 5,
                                        FinalizeBlock = 7
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
                                    new()
                                    {
                                        Addresses = GenerateValidators(7),
                                        InitializeBlock = 10,
                                        FinalizeBlock = 11
                                    }
                                },
                            }
                        }
                    },
                    TestName = "consecutive_initiate_change_reorganisation_finalizes_not_reorganised_initiate_change",
                })
                {
                    TestName = "consecutive_initiate_change_reorganisation_finalizes_not_reorganised_initiate_change",
                };
            }
        }

        [TestCaseSource(nameof(ConsecutiveInitiateChangeData))]
        public void consecutive_initiate_change_gets_finalized_and_switch_validators(ConsecutiveInitiateChangeTestParameters test)
        {
            Dictionary<int, int> hashSeeds = new();

            Address[] currentValidators = GenerateValidators(1);
            SetupInitialValidators(currentValidators);

            IAuRaValidator validator = new ContractBasedValidator(_validatorContract, _blockTree, _receiptsStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, _blockTree.Head.Header, _logManager, test.StartBlockNumber);

            test.TryDoReorganisations(test.StartBlockNumber, out _);
            for (int i = 0; i < test.Current.NumberOfSteps; i++)
            {
                int blockNumber = test.Current.BlockNumber + i;

                if (test.TryDoReorganisations(blockNumber, out ConsecutiveInitiateChangeTestParameters.ChainInfo lastChain))
                {
                    ValidateFinalizationForChain(lastChain);
                    i = 0;
                    blockNumber = test.Current.BlockNumber + i;
                }

                if (hashSeeds.TryGetValue(blockNumber, out int value))
                    value++;
                else
                    hashSeeds[blockNumber] = 0;

                _block.Header.Number = blockNumber;
                _block.Header.Beneficiary = currentValidators[blockNumber % currentValidators.Length];
                _block.Header.AuRaStep = blockNumber;
                _block.Header.Hash = Keccak.Compute((blockNumber + hashSeeds[blockNumber]).ToString());
                _block.Header.ParentHash = blockNumber == test.StartBlockNumber ? Keccak.Zero : Keccak.Compute((blockNumber - 1 + hashSeeds[blockNumber - 1]).ToString());

                TxReceipt[] txReceipts = test.GetReceipts(_validatorContract, _block, _contractAddress, _abiEncoder, SetupAbiAddresses);

                Keccak? blockHashForClosure = _block.Hash;
                _receiptsStorage.Get(Arg.Is<Block>(b => b.Hash == blockHashForClosure)).Returns(txReceipts);

                _block.Header.Bloom = new Bloom(txReceipts.SelectMany(r => r.Logs).ToArray());

                _blockTree.FindBlock(_block.Header.Hash, Arg.Any<BlockTreeLookupOptions>()).Returns(new Block(_block.Header.Clone()));

                Action preProcess = () => validator.OnBlockProcessingStart(_block);
                preProcess.Should().NotThrow<InvalidOperationException>(test.TestName);
                validator.OnBlockProcessingEnd(_block, txReceipts);
                int finalizedNumber = blockNumber - validator.Validators.MinSealersForFinalization() + 1;
                _blockFinalizationManager.GetLastLevelFinalizedBy(_block.Header.Hash).Returns(finalizedNumber);
                _blockFinalizationManager.BlocksFinalized += Raise.EventWith(
                    new FinalizeEventArgs(_block.Header, Build.A.BlockHeader.WithNumber(finalizedNumber)
                            .WithHash(Keccak.Compute((finalizedNumber + hashSeeds[finalizedNumber]).ToString())).TestObject));

                currentValidators = test.GetCurrentValidators(blockNumber);
                validator.Validators.Should().BeEquivalentTo(currentValidators, o => o.WithStrictOrdering(), $"Validator address should be recognized in block {blockNumber}");
            }

            ValidateFinalizationForChain(test.Current);
        }

        [TestCase(8, 5, null)]
        [TestCase(7, 5, 7)]
        [TestCase(6, 5, 7)]
        [TestCase(5, 5, 7)]
        [TestCase(4, 4, 7)]
        [TestCase(2, 2, 4)]
        [TestCase(1, 1, 4)]
        [TestCase(1, 7, null)]
        public void nonconsecutive_non_producing_preProcess_loads_pending_validators_from_receipts(int lastLevelFinalized, int initialValidatorsIndex, int? expectedBlockValidators)
        {
            IEnumerable<Block> GetAllBlocks(BlockTree bt)
            {

                Block? block = bt.FindBlock(bt.Head.Hash, BlockTreeLookupOptions.None);
                while (block is not null)
                {
                    yield return block;
                    block = bt.FindBlock(block.ParentHash, BlockTreeLookupOptions.None);
                }
            }

            Address validators = TestItem.Addresses[initialValidatorsIndex * 10];
            InMemoryReceiptStorage inMemoryReceiptStorage = new();
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(RopstenSpecProvider.Instance)
                .WithTransactions(inMemoryReceiptStorage, delegate (Block block, Transaction transaction)
                    {
                        byte i = 0;
                        return new[]
                        {
                            Build.A.LogEntry.WithAddress(_contractAddress)
                                .WithData(new[] {(byte) (block.Number * 10 + i++)})
                                .WithTopics(_validatorContract.AbiDefinition.Events[ValidatorContract.InitiateChange].GetHash(), block.ParentHash)
                                .TestObject
                        };
                    })
                .OfChainLength(9, 0, 0, false, validators);

            BlockTree blockTree = blockTreeBuilder.TestObject;
            SetupInitialValidators(blockTree.Head?.Header, blockTree.FindHeader(blockTree.Head?.ParentHash, BlockTreeLookupOptions.None), validators);
            IAuRaValidator validator = new ContractBasedValidator(_validatorContract, blockTree, inMemoryReceiptStorage, _validatorStore, _validSealerStrategy, _blockFinalizationManager, _parentHeader, _logManager, 1);

            _abiEncoder.Decode(_validatorContract.AbiDefinition.Functions[ValidatorContract.GetValidatorsFunction].GetReturnInfo(), Arg.Any<byte[]>())
                .Returns(c =>
                {
                    byte addressIndex = c.Arg<byte[]>()[0];
                    return new object[] { new Address[] { TestItem.Addresses[addressIndex] } };
                });

            _blockFinalizationManager.GetLastLevelFinalizedBy(blockTree.Head.ParentHash).Returns(lastLevelFinalized);

            validator.OnBlockProcessingStart(blockTree.FindBlock(blockTree.Head.Hash, BlockTreeLookupOptions.None));

            PendingValidators pendingValidators = null;
            if (expectedBlockValidators.HasValue)
            {
                Block block = GetAllBlocks(blockTree).First(b => b.Number == expectedBlockValidators.Value);
                pendingValidators = new PendingValidators(block.Number, block.Hash, new[] { TestItem.Addresses[block.Number * 10] });
            }

            _validatorStore.PendingValidators.Should().BeEquivalentTo(pendingValidators);
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
            Enumerable.Range(1, number).Select(i => Address.FromNumber((UInt256)i)).ToArray();

        private void SetupInitialValidators(params Address[] initialValidators)
        {
            SetupInitialValidators(_block.Header, initialValidators);
        }

        private void SetupInitialValidators(BlockHeader header, params Address[] initialValidators)
        {
            SetupInitialValidators(header, null, initialValidators);
        }

        private void SetupInitialValidators(BlockHeader header, BlockHeader parentHeader, params Address[] initialValidators)
        {
            _initialValidators = initialValidators;

            if (parentHeader is null)
            {
                parentHeader = _parentHeader = Build.A.BlockHeader.WithNumber(header.Number - 1).TestObject;
                _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.None).Returns(_parentHeader);
            }

            _transactionProcessor.When(x => x.CallAndRestore(
                    Arg.Is<Transaction>(t => CheckTransaction(t, _getValidatorsData)),
                    Arg.Any<BlockHeader>(),
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
                data).Returns(new object[] { addresses });

            return data;
        }

        private bool CheckTransaction(Transaction t, (Address Sender, byte[] TransactionData) transactionInfo)
        {
            return t.SenderAddress == transactionInfo.Sender && t.To == _contractAddress && t.Data.AsArray() == transactionInfo.TransactionData;
        }

        public class ConsecutiveInitiateChangeTestParameters
        {
            private ChainInfo _last;

            public int StartBlockNumber { get; set; }

            public ChainInfo Current { get; set; }

            public IDictionary<long, ChainInfo> Reorganisations { get; set; }

            public string TestName { get; set; }

            public override string ToString() => JsonSerializer.Serialize(this);

            public bool TryDoReorganisations(int blockNumber, out ChainInfo last)
            {
                if (Reorganisations.TryGetValue(blockNumber, out ChainInfo chainInfo))
                {
                    _last = last = Current;
                    Current = chainInfo;
                    Reorganisations.Remove(blockNumber);
                    return true;
                }

                last = null;
                return false;
            }

            public TxReceipt[] GetReceipts(ValidatorContract validatorContract, Block block, Address contractAddress, IAbiEncoder encoder, Func<Address[], byte[]> dataFunc)
            {
                Address[] validators = Current.Validators?.FirstOrDefault(v => v.InitializeBlock == block.Number)?.Addresses;
                if (validators is null)
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    LogEntry[] logs = new[]
                    {
                        new LogEntry(contractAddress,
                            dataFunc(validators),
                            new[] {validatorContract.AbiDefinition.Events[ValidatorContract.InitiateChange].GetHash(), block.ParentHash})
                    };

                    return new TxReceipt[]
                    {
                        new()
                        {
                            Logs = logs,
                            Bloom = new Bloom(logs)
                        }
                    };
                }
            }

            public Address[] GetCurrentValidators(int blockNumber)
            {
                ValidatorsInfo LastFinalizedInitChange(ChainInfo chainInfo, int maxInitializeBlockNumber = int.MaxValue) => chainInfo?.Validators?.LastOrDefault(v => v.FinalizeBlock <= blockNumber && v.InitializeBlock < maxInitializeBlockNumber);

                ValidatorsInfo finalizedInitChange = LastFinalizedInitChange(Current);
                ValidatorsInfo previousReorgFinalizedInitChange = LastFinalizedInitChange(_last, Current.BlockNumber);
                ValidatorsInfo lastFinalizedInitChange = finalizedInitChange ?? previousReorgFinalizedInitChange;
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
