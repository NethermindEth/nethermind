// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Trie;
using Nethermind.TxPool;
using Block = Nethermind.Core.Block;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Filters;
using Nethermind.State;
using Nethermind.Core.Extensions;
using Nethermind.Config;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Facade.Proxy.Models.MultiCall;
using System.Transactions;
using Microsoft.CSharp.RuntimeBinder;
using Transaction = Nethermind.Core.Transaction;
using Nethermind.Specs;

namespace Nethermind.Facade
{
    public interface IBlockchainBridgeFactory
    {
        IBlockchainBridge CreateBlockchainBridge();
    }

    [Todo(Improve.Refactor, "I want to remove BlockchainBridge, split it into something with logging, state and tx processing. Then we can start using independent modules.")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IReadOnlyTxProcessingEnv _processingEnv;
        private readonly IMultiCallBlocksProcessingEnv _multiCallProcessingEnv;
        private readonly ITxPool _txPool;
        private readonly IFilterStore _filterStore;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITimestamper _timestamper;
        private readonly IFilterManager _filterManager;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ILogFinder _logFinder;
        private readonly ISpecProvider _specProvider;
        private readonly IBlocksConfig _blocksConfig;

        public BlockchainBridge(IReadOnlyTxProcessingEnv processingEnv,
            IMultiCallBlocksProcessingEnv multiCallProcessingEnv,
            ITxPool? txPool,
            IReceiptFinder? receiptStorage,
            IFilterStore? filterStore,
            IFilterManager? filterManager,
            IEthereumEcdsa? ecdsa,
            ITimestamper? timestamper,
            ILogFinder? logFinder,
            ISpecProvider specProvider,
            IBlocksConfig blocksConfig,
            bool isMining)
        {
            _processingEnv = processingEnv ?? throw new ArgumentNullException(nameof(processingEnv));
            _multiCallProcessingEnv = multiCallProcessingEnv ?? throw new ArgumentNullException(nameof(multiCallProcessingEnv));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(_txPool));
            _receiptFinder = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentNullException(nameof(filterManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blocksConfig = blocksConfig;
            IsMining = isMining;
        }

        public Block? HeadBlock
        {
            get
            {
                return _processingEnv.BlockTree.Head;
            }
        }

        public bool IsMining { get; }

        public (TxReceipt Receipt, UInt256? EffectiveGasPrice, int LogIndexStart) GetReceiptAndEffectiveGasPrice(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                Block? block = _processingEnv.BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null)
                {
                    TxReceipt[] txReceipts = _receiptFinder.Get(block);
                    TxReceipt txReceipt = txReceipts.ForTransaction(txHash);
                    int logIndexStart = txReceipts.GetBlockLogFirstIndex(txReceipt.Index);
                    Transaction tx = block.Transactions[txReceipt.Index];
                    bool is1559Enabled = _specProvider.GetSpecFor1559(block.Number).IsEip1559Enabled;
                    UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(is1559Enabled, block.Header.BaseFeePerGas);
                    return (txReceipt, effectiveGasPrice, logIndexStart);
                }
            }

            return (null, null, 0);
        }

        public (TxReceipt Receipt, Transaction Transaction, UInt256? baseFee) GetTransaction(Keccak txHash)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash is not null)
            {
                Block block = _processingEnv.BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
                return (txReceipt, block?.Transactions[txReceipt.Index], block?.BaseFeePerGas);
            }

            if (_txPool.TryGetPendingTransaction(txHash, out Transaction? transaction))
            {
                return (null, transaction, null);
            }

            return (null, null, null);
        }

        public TxReceipt? GetReceipt(Keccak txHash)
        {
            Keccak? blockHash = _receiptFinder.FindBlockHash(txHash);
            return blockHash is not null ? _receiptFinder.Get(blockHash).ForTransaction(txHash) : null;
        }

        public class CallOutput
        {
            public CallOutput()
            {
            }

            public CallOutput(byte[] outputData, long gasSpent, string error, bool inputError = false)
            {
                Error = error;
                OutputData = outputData;
                GasSpent = gasSpent;
                InputError = inputError;
            }

            public string? Error { get; set; }

            public byte[] OutputData { get; set; }

            public long GasSpent { get; set; }

            public bool InputError { get; set; }

            public AccessList? AccessList { get; set; }
        }

        public CallOutput Call(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            CallOutputTracer callOutputTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, tx, false,
                callOutputTracer.WithCancellation(cancellationToken));
            return new CallOutput
            {
                Error = tryCallResult.Success ? callOutputTracer.Error : tryCallResult.Error,
                GasSpent = callOutputTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success
            };
        }


        public List<MultiCallBlockResult> MultiCall(BlockHeader header, MultiCallBlockStateCallsModel[] blocks, CancellationToken cancellationToken)
        {
            MultiCallBlockTracer multiCallOutputTracer = new();

            MultiCallTrace(header, blocks, multiCallOutputTracer.WithCancellation(cancellationToken));

            return multiCallOutputTracer._results;
        }

        public CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            using IReadOnlyTransactionProcessor? readOnlyTransactionProcessor = _processingEnv.Build(header.StateRoot!);

            EstimateGasTracer estimateGasTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(
                header,
                tx,
                true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(readOnlyTransactionProcessor, _processingEnv.StateProvider,
                _specProvider, _blocksConfig);
            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer);

            return new CallOutput
            {
                Error = tryCallResult.Success ? estimateGasTracer.Error : tryCallResult.Error,
                GasSpent = estimate,
                InputError = !tryCallResult.Success
            };
        }

        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, CancellationToken cancellationToken, bool optimize)
        {
            CallOutputTracer callOutputTracer = new();
            AccessTxTracer accessTxTracer = optimize
                ? new(tx.SenderAddress,
                    tx.GetRecipient(tx.IsContractCreation ? _processingEnv.StateReader.GetNonce(header.StateRoot, tx.SenderAddress) : 0))
                : new();

            (bool Success, string Error) tryCallResult = TryCallAndRestore(header, tx, false,
                new CompositeTxTracer(callOutputTracer, accessTxTracer).WithCancellation(cancellationToken));

            return new CallOutput
            {
                Error = tryCallResult.Success ? callOutputTracer.Error : tryCallResult.Error,
                GasSpent = accessTxTracer.GasSpent,
                OutputData = callOutputTracer.ReturnValue,
                InputError = !tryCallResult.Success,
                AccessList = accessTxTracer.AccessList
            };
        }

        private (bool Success, string Error) TryCallAndRestore(
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                CallAndRestore(blockHeader, transaction, treatBlockHeaderAsParentBlock, tracer);
                return (true, string.Empty);
            }
            catch (InsufficientBalanceException ex)
            {
                return (false, ex.Message);
            }
        }

        private void CallAndRestore(
            BlockHeader blockHeader,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            transaction.SenderAddress ??= Address.SystemUser;

            Keccak stateRoot = blockHeader.StateRoot!;
            using IReadOnlyTransactionProcessor transactionProcessor = _processingEnv.Build(stateRoot);

            if (transaction.Nonce == 0)
            {
                transaction.Nonce = GetNonce(stateRoot, transaction.SenderAddress);
            }

            BlockHeader callHeader = treatBlockHeaderAsParentBlock
                ? new(
                    blockHeader.Hash!,
                    Keccak.OfAnEmptySequenceRlp,
                    Address.Zero,
                    UInt256.Zero,
                    blockHeader.Number + 1,
                    blockHeader.GasLimit,
                    Math.Max(blockHeader.Timestamp + 1, _timestamper.UnixTime.Seconds),
                    Array.Empty<byte>(),
                    null)
                : new(
                    blockHeader.ParentHash!,
                    blockHeader.UnclesHash!,
                    blockHeader.Beneficiary!,
                    blockHeader.Difficulty,
                    blockHeader.Number,
                    blockHeader.GasLimit,
                    blockHeader.Timestamp,
                    blockHeader.ExtraData,
                    blockHeader.ExcessDataGas);

            IReleaseSpec releaseSpec = _specProvider.GetSpec(callHeader);
            callHeader.BaseFeePerGas = treatBlockHeaderAsParentBlock
                ? BaseFeeCalculator.Calculate(blockHeader, releaseSpec)
                : blockHeader.BaseFeePerGas;

            if (releaseSpec.IsEip4844Enabled)
            {
                // TODO: Calculate ExcessDataGas depending on parent ExcessDataGas and number of blobs in txs
                callHeader.ExcessDataGas = treatBlockHeaderAsParentBlock
                    ? 0
                    : blockHeader.ExcessDataGas;
            }
            callHeader.MixHash = blockHeader.MixHash;
            callHeader.IsPostMerge = blockHeader.Difficulty == 0;
            transaction.Hash = transaction.CalculateHash();
            transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
        }

        public void MultiCallTrace(BlockHeader parent, MultiCallBlockStateCallsModel[] blocks,
           IBlockTracer tracer)
        {
            using (IMultiCallBlocksProcessingEnv? env = _multiCallProcessingEnv.Clone())
            {
                var processor = env.GetProcessor(env.StateProvider.StateRoot);
                var firstBlock = blocks.FirstOrDefault();
                var startStateRoot = parent.StateRoot;
                if (firstBlock?.BlockOverride?.Number != null
                    && firstBlock?.BlockOverride?.Number > UInt256.Zero
                    && firstBlock?.BlockOverride?.Number < (ulong)long.MaxValue)
                {
                    BlockHeader? searchResult =
                        _multiCallProcessingEnv.BlockTree.FindHeader((long)firstBlock?.BlockOverride.Number);
                    if (searchResult != null)
                    {
                        startStateRoot = searchResult.StateRoot;
                    }
                }

                foreach (MultiCallBlockStateCallsModel? callInputBlock in blocks)
                {
                    BlockHeader callHeader = null;
                    if (callInputBlock.BlockOverride == null)
                    {
                        callHeader = new BlockHeader(
                            parent.Hash,
                            Keccak.OfAnEmptySequenceRlp,
                            Address.Zero,
                            UInt256.Zero,
                            parent.Number + 1,
                            parent.GasLimit,
                            parent.Timestamp + 1,
                            Array.Empty<byte>());
                        callHeader.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(parent));
                    }
                    else
                    {
                        callHeader = callInputBlock.BlockOverride.GetBlockHeader(parent);
                    }

                    env.StateProvider.StateRoot = parent.StateRoot;

                    IReleaseSpec releaseSpec = _specProvider.GetSpec(parent);

                    if (releaseSpec.IsEip4844Enabled)
                    {
                        // TODO: Calculate ExcessDataGas depending on parent ExcessDataGas and number of blobs in txs
                        callHeader.ExcessDataGas = 0;
                    }

                    callHeader.MixHash = parent.MixHash;
                    callHeader.IsPostMerge = parent.Difficulty == 0;

                    var transactions = callInputBlock.Calls.Select(model => model.GetTransaction()).ToList();
                    foreach (Transaction transaction in transactions)
                    {
                        transaction.SenderAddress ??= Address.SystemUser;

                        Keccak stateRoot = callHeader.StateRoot!;

                        if (transaction.Nonce == 0)
                        {
                            transaction.Nonce = env.StateProvider.GetAccount(transaction.SenderAddress).Nonce;
                        }

                        transaction.Hash = transaction.CalculateHash();
                    }

                    Block? currentBlock = new(callHeader, transactions, Array.Empty<BlockHeader>());

                    var currentSpec = env.SpecProvider.GetSpec(currentBlock.Header);
                    if (callInputBlock.StateOverrides != null)
                    {
                        ModifyAccounts(callInputBlock.StateOverrides, env.StateProvider, currentSpec);
                    }

                    env.StateProvider.Commit(currentSpec);
                    env.StateProvider.CommitTree(currentBlock.Number);
                    env.StateProvider.RecalculateStateRoot();

                    currentBlock.Header.StateRoot = env.StateProvider.StateRoot;
                    currentBlock.Header.IsPostMerge = true; //ToDo: Seal if necessary before merge 192 BPB
                    currentBlock.Header.Hash = currentBlock.Header.CalculateHash();


                    Block[]? currentBlocks = processor.Process(env.StateProvider.StateRoot,
                        new List<Block> { currentBlock },
                        ProcessingOptions.ForceProcessing |
                        // ProcessingOptions.NoValidation |
                        ProcessingOptions.DoNotVerifyNonce |
                        ProcessingOptions.IgnoreParentNotOnMainChain |
                        ProcessingOptions.MarkAsProcessed |
                        ProcessingOptions.StoreReceipts
                        , tracer);

                    var processedBlock = currentBlocks.FirstOrDefault();
                    if (processedBlock != null)
                    {
                        parent = processedBlock.Header;
                        //env.StateProvider.CommitTree(parent.Number);
                    }
                    else
                    {
                        throw new RuntimeBinderException("Processing failed"); //Todo fix
                    }
                }
            }
        }

        public ulong GetChainId()
        {
            return _processingEnv.BlockTree.ChainId;
        }

        private UInt256 GetNonce(Keccak stateRoot, Address address)
        {
            return _processingEnv.StateReader.GetNonce(stateRoot, address);
        }

        //Apply changes to accounts and contracts states including precompiles
        private void ModifyAccounts(AccountOverride[] StateOverrides, IWorldState? StateProvider, IReleaseSpec? CurrentSpec)
        {
            Account? acc;

            foreach (AccountOverride accountOverride in StateOverrides)
            {
                Address address = accountOverride.Address;
                bool accExists = StateProvider.AccountExists(address);
                if (!accExists)
                {
                    StateProvider.CreateAccount(address, accountOverride.Balance, accountOverride.Nonce);
                    acc = StateProvider.GetAccount(address);
                }
                else
                    acc = StateProvider.GetAccount(address);

                UInt256 accBalance = acc.Balance;
                if (accBalance > accountOverride.Balance)
                    StateProvider.SubtractFromBalance(address, accBalance - accountOverride.Balance, CurrentSpec);
                else if (accBalance < accountOverride.Nonce)
                    StateProvider.AddToBalance(address, accountOverride.Balance - accBalance, CurrentSpec);

                UInt256 accNonce = acc.Nonce;
                if (accNonce > accountOverride.Nonce)
                    StateProvider.DecrementNonce(address);
                else if (accNonce < accountOverride.Nonce) StateProvider.IncrementNonce(address);

                if (acc != null)
                {
                    if (accountOverride.Code is not null)
                    {
                        _multiCallProcessingEnv.Machine.SetOverwrite(StateProvider, CurrentSpec, address,
                            new CodeInfo(accountOverride.Code), accountOverride.MoveToAddress);
                    }
                }


                if (accountOverride.State is not null)
                {
                    accountOverride.State = new Dictionary<UInt256, byte[]>();
                    foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.State)
                        StateProvider.Set(new StorageCell(address, storage.Key),
                            storage.Value.WithoutLeadingZeros().ToArray());
                }

                if (accountOverride.StateDiff is not null)
                {
                    foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.StateDiff)
                        StateProvider.Set(new StorageCell(address, storage.Key),
                            storage.Value.WithoutLeadingZeros().ToArray());
                }
                StateProvider.Commit(CurrentSpec);
            }
        }

        public bool FilterExists(int filterId) => _filterStore.FilterExists(filterId);
        public FilterType GetFilterType(int filterId) => _filterStore.GetFilterType(filterId);
        public FilterLog[] GetFilterLogs(int filterId) => _filterManager.GetLogs(filterId);

        public IEnumerable<FilterLog> GetLogs(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null,
            CancellationToken cancellationToken = default)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return _logFinder.FindLogs(filter, cancellationToken);
        }

        public bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default)
        {
            LogFilter? filter;
            filterLogs = null;
            if ((filter = _filterStore.GetFilter<LogFilter>(filterId)) is not null)
                filterLogs = _logFinder.FindLogs(filter, cancellationToken);

            return filter is not null;
        }

        public int NewFilter(BlockParameter fromBlock, BlockParameter toBlock,
            object? address = null, IEnumerable<object>? topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = _filterStore.CreateBlockFilter(_processingEnv.BlockTree.Head!.Number);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewPendingTransactionFilter()
        {
            PendingTransactionFilter filter = _filterStore.CreatePendingTransactionFilter();
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public void UninstallFilter(int filterId) => _filterStore.RemoveFilter(filterId);
        public FilterLog[] GetLogFilterChanges(int filterId) => _filterManager.PollLogs(filterId);
        public Keccak[] GetBlockFilterChanges(int filterId) => _filterManager.PollBlockHashes(filterId);

        public void RecoverTxSenders(Block block)
        {
            TxReceipt[] receipts = _receiptFinder.Get(block);
            if (block.Transactions.Length == receipts.Length)
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction transaction = block.Transactions[i];
                    TxReceipt receipt = receipts[i];
                    transaction.SenderAddress ??= receipt.Sender ?? RecoverTxSender(transaction);
                }
            }
            else
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction transaction = block.Transactions[i];
                    transaction.SenderAddress ??= RecoverTxSender(transaction);
                }
            }
        }

        public Keccak[] GetPendingTransactionFilterChanges(int filterId) =>
            _filterManager.PollPendingTransactionHashes(filterId);

        public Address? RecoverTxSender(Transaction tx) => _ecdsa.RecoverAddress(tx);

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)
        {
            _processingEnv.StateReader.RunTreeVisitor(treeVisitor, stateRoot);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            return _logFinder.FindLogs(filter, cancellationToken);
        }
    }
}
