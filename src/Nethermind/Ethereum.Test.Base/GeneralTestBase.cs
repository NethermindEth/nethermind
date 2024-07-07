// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Evm.Tracing.GethStyle.Custom;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Ethereum.Test.Base
{
    public abstract class GeneralStateTestBase
    {
        private static ILogger _logger = new(new ConsoleAsyncLogger(LogLevel.Info));
        private static ILogManager _logManager = LimboLogs.Instance;
        private static readonly UInt256 _defaultBaseFeeForStateTest = 0xA;
        private readonly TxValidator _txValidator = new(MainnetSpecProvider.Instance.ChainId);

        [SetUp]
        public void Setup()
        {
        }

        [OneTimeSetUp]
        public Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

        protected static void Setup(ILogManager logManager)
        {
            _logManager = logManager ?? LimboLogs.Instance;
            _logger = _logManager.GetClassLogger();
        }

        protected EthereumTestResult RunTest(GeneralStateTest test)
        {
            return RunTest(test, NullTxTracer.Instance);
        }

        protected EthereumTestResult RunTest(GeneralStateTest test, ITxTracer tracer)
        {
            TestContext.Write($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();

            ISpecProvider specProvider = new CustomSpecProvider(
                ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
                ((ForkActivation)1, test.Fork));

            if (specProvider.GenesisSpec != Frontier.Instance)
            {
                Assert.Fail("Expected genesis spec to be Frontier for blockchain tests");
            }

            TrieStore trieStore = new(stateDb, _logManager);
            WorldState stateProvider = new(trieStore, codeDb, _logManager);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            CodeInfoRepository codeInfoRepository = new();
            IVirtualMachine virtualMachine = new VirtualMachine(
                blockhashProvider,
                specProvider,
                codeInfoRepository,
                _logManager);

            TransactionProcessor transactionProcessor = new(
                specProvider,
                stateProvider,
                virtualMachine,
                codeInfoRepository,
                _logManager);

            InitializeTestPreState(test.Pre, stateProvider, specProvider);

            BlockHeader header = new(
                test.PreviousHash,
                Keccak.OfAnEmptySequenceRlp,
                test.CurrentCoinbase,
                test.CurrentDifficulty,
                test.CurrentNumber,
                test.CurrentGasLimit,
                test.CurrentTimestamp,
                []);
            header.BaseFeePerGas = test.Fork.IsEip1559Enabled ? test.CurrentBaseFee ?? _defaultBaseFeeForStateTest : UInt256.Zero;
            header.StateRoot = test.PostHash;
            header.Hash = header.CalculateHash();
            header.IsPostMerge = test.CurrentRandom is not null;
            header.MixHash = test.CurrentRandom;
            header.WithdrawalsRoot = test.CurrentWithdrawalsRoot;
            header.ParentBeaconBlockRoot = test.CurrentBeaconRoot;
            header.ExcessBlobGas = test.CurrentExcessBlobGas ?? (test.Fork is Cancun ? 0ul : null);
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(test.Transactions);

            Stopwatch stopwatch = Stopwatch.StartNew();
            IReleaseSpec? spec = specProvider.GetSpec((ForkActivation)test.CurrentNumber);

            foreach (var transaction in test.Transactions)
            {
                transaction.ChainId ??= MainnetSpecProvider.Instance.ChainId;
            }

            if (test.ParentBlobGasUsed is not null && test.ParentExcessBlobGas is not null)
            {
                BlockHeader parent = new(
                    parentHash: Keccak.Zero,
                    unclesHash: Keccak.OfAnEmptySequenceRlp,
                    beneficiary: test.CurrentCoinbase,
                    difficulty: test.CurrentDifficulty,
                    number: test.CurrentNumber - 1,
                    gasLimit: test.CurrentGasLimit,
                    timestamp: test.CurrentTimestamp,
                    extraData: []
                )
                {
                    BlobGasUsed = (ulong)test.ParentBlobGasUsed,
                    ExcessBlobGas = (ulong)test.ParentExcessBlobGas,
                };
                header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            }

            Block block = Build.A.Block.WithTransactions(test.Transactions).WithHeader(header).TestObject;

            T8NToolTracer? txTracer = null;
            if (tracer is T8NToolTracer)
            {
                txTracer = (T8NToolTracer)tracer;
            }
            txTracer?.StartNewBlockTrace(block);
            List<RejectedTx> rejectedTxReceipts = [];
            int txIndex = 0;
            List<Transaction> includedTx = [];
            List<Transaction> successfulTxs = [];
            List<TxReceipt> includedTxReceipts = [];

            foreach (var tx in test.Transactions)
            {
                bool isValid = _txValidator.IsWellFormed(tx, spec) && IsValidBlock(block, specProvider);
                if (isValid)
                {
                    txTracer?.StartNewTxTrace(tx);
                    TransactionResult transactionResult = transactionProcessor.Execute(tx, new BlockExecutionContext(header), tracer);
                    txTracer?.EndTxTrace();
                    if (txTracer == null) continue;
                    includedTx.Add(tx);
                    if (transactionResult.Success)
                    {
                        successfulTxs.Add(tx);
                        txTracer.LastReceipt.PostTransactionState = null;
                        txTracer.LastReceipt.BlockHash = null;
                        txTracer.LastReceipt.BlockNumber = 0;
                        includedTxReceipts.Add(txTracer.LastReceipt);
                    }
                    else if (transactionResult.Error != null)
                    {
                        rejectedTxReceipts.Add(new RejectedTx(txIndex, GethErrorMappings.GetErrorMapping(transactionResult.Error, tx.SenderAddress.ToString(true), tx.Nonce, stateProvider.GetNonce(tx.SenderAddress))));
                        stateProvider.Reset();
                    }
                    stateProvider.RecalculateStateRoot();
                    txIndex++;
                }
            }

            stopwatch.Stop();

            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
            stateProvider.CommitTree(1);

            // '@winsvega added a 0-wei reward to the miner , so we had to add that into the state test execution phase. He needed it for retesteth.'
            if (!stateProvider.AccountExists(test.CurrentCoinbase))
            {
                stateProvider.CreateAccount(test.CurrentCoinbase, 0);
            }

            stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));

            stateProvider.RecalculateStateRoot();

            List<string> differences = RunAssertions(test, stateProvider);
            EthereumTestResult testResult = new(test.Name, test.ForkName, differences.Count == 0);
            testResult.TimeInMs = stopwatch.Elapsed.TotalMilliseconds;
            testResult.StateRoot = stateProvider.StateRoot;

            if (test.Name == "T8N")
            {
                Hash256 txRoot = TxTrie.CalculateRoot(successfulTxs.ToArray());
                IReceiptSpec receiptSpec = specProvider.GetSpec(header);
                Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec, includedTxReceipts.ToArray(), new ReceiptMessageDecoder());
                var logEntries = includedTxReceipts.SelectMany(receipt => receipt.Logs ?? Enumerable.Empty<LogEntry>()).ToArray();
                var bloom = new Bloom(logEntries);
                ulong gasUsed = 0;
                if (!txTracer.TxReceipts.IsNullOrEmpty())
                {
                    gasUsed = (ulong)txTracer.LastReceipt.GasUsedTotal;
                }
                testResult.TxRoot = txRoot;
                testResult.ReceiptsRoot = receiptsRoot;
                testResult.LogsBloom = bloom;
                testResult.LogsHash = Keccak.Compute(Rlp.OfEmptySequence.Bytes);
                testResult.Receipts = includedTxReceipts.ToArray();
                testResult.Rejected = rejectedTxReceipts.IsNullOrEmpty() ? null : rejectedTxReceipts.ToArray();
                testResult.CurrentDifficulty = test.CurrentDifficulty;
                testResult.GasUsed = new UInt256(gasUsed);
                testResult.CurrentBaseFee = test.CurrentBaseFee;
                testResult.WithdrawalsRoot = block.WithdrawalsRoot;
                testResult.CurrentExcessBlobGas = header.ExcessBlobGas;
                testResult.BlobGasUsed = header.BlobGasUsed;

                var accounts = test.Pre.Keys.ToDictionary(address => address,
                    address => ConvertAccountToNativePrestateTracerAccount(address, stateProvider, txTracer.storages));
                foreach (Ommer ommer in test.Ommers)
                {
                    accounts.Add(ommer.Address, ConvertAccountToNativePrestateTracerAccount(ommer.Address, stateProvider, txTracer.storages));
                }
                if (header.Beneficiary != null)
                {
                    accounts.Add(header.Beneficiary, ConvertAccountToNativePrestateTracerAccount(header.Beneficiary, stateProvider, txTracer.storages));
                }

                testResult.Accounts = accounts;
                testResult.TransactionsRlp = Rlp.Encode(successfulTxs.ToArray()).Bytes;
            }

            //            Assert.Zero(differences.Count, "differences");
            return testResult;
        }

        private NativePrestateTracerAccount ConvertAccountToNativePrestateTracerAccount(Address address, WorldState stateProvider, Dictionary<Address, Dictionary<UInt256, UInt256>> storages)
        {
            var account = stateProvider.GetAccount(address);
            var code = stateProvider.GetCode(address);
            var accountState = new NativePrestateTracerAccount(account.Balance, account.Nonce, code);

            if (storages.TryGetValue(address, out var storage))
            {
                accountState.Storage = storage;
            }

            return accountState;
        }

        public static void InitializeTestPreState(Dictionary<Address, AccountState> pre, WorldState stateProvider, ISpecProvider specProvider)
        {
            foreach (KeyValuePair<Address, AccountState> accountState in pre)
            {
                if (accountState.Value.Storage is not null)
                {
                    foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
                    {
                        stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key),
                            storageItem.Value.WithoutLeadingZeros().ToArray());
                    }
                }

                stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance, accountState.Value.Nonce);
                if (accountState.Value.Code is not null)
                {
                    stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
                }
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);
            stateProvider.Reset();
        }

        private bool IsValidBlock(Block block, ISpecProvider specProvider)
        {
            IBlockTree blockTree = Build.A.BlockTree()
                .WithSpecProvider(specProvider)
                .WithoutSettingHead
                .TestObject;

            var difficultyCalculator = new EthashDifficultyCalculator(specProvider);
            var sealer = new EthashSealValidator(_logManager, difficultyCalculator, new CryptoRandom(), new Ethash(_logManager), Timestamper.Default);
            IHeaderValidator headerValidator = new HeaderValidator(blockTree, sealer, specProvider, _logManager);
            IUnclesValidator unclesValidator = new UnclesValidator(blockTree, headerValidator, _logManager);
            IBlockValidator blockValidator = new BlockValidator(_txValidator, headerValidator, unclesValidator, specProvider, _logManager);

            return blockValidator.ValidateOrphanedBlock(block, out _);
        }

        private List<string> RunAssertions(GeneralStateTest test, IWorldState stateProvider)
        {
            List<string> differences = [];
            if (test.PostHash != stateProvider.StateRoot)
            {
                differences.Add($"STATE ROOT exp: {test.PostHash}, actual: {stateProvider.StateRoot}");
            }

            foreach (string difference in differences)
            {
                _logger.Info(difference);
            }

            return differences;
        }
    }
}
