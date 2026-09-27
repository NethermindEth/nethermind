// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;

[assembly: InternalsVisibleTo("Nethermind.Stateless.Executor")]

namespace Nethermind.Consensus.Stateless;

public class StatelessBlockProcessingEnv(
    Witness witness,
    ISpecProvider specProvider,
    ISealValidator sealValidator,
    ILogManager logManager)
{
    private IBlockProcessor? _blockProcessor;
    private IBlockValidator? _blockValidator;
    private IWorldState? _worldState;
    private StatelessBlockTree? _blockTree;
    private BlockHeader? _parentHeader;

    // Per-block: StaticCodeCache.Instance would leak code across blocks and mask deliberately missing
    // witness code. The first fetch of each hash still reads through the world state.
    private readonly StaticCodeCache _codeCache = new(CodeCacheCapacity);

    // A block touches a few hundred distinct hashes; MemoryAllowance.CodeCacheSize would round up to
    // ~0.4 MB zeroed per block (LOH on the host). Overflow only costs a re-read.
    private const int CodeCacheCapacity = 512;

    /// <summary>Controls whether replay records derived requests or preserves the supplied requests hash.</summary>
    public IExecutionRequestsProcessorFactory ExecutionRequestsProcessorFactory { get; init; } = StatelessExecutionRequestsProcessorFactory.Instance;

    /// <summary>Builds the transaction processors; the default executes the Ethereum rules.</summary>
    public ITransactionProcessorFactory? TransactionProcessorFactory { get; init; }

    /// <summary>Validates block transactions; the default knows only the Ethereum transaction types.</summary>
    public ITxValidator? TxValidator { get; init; }

    /// <summary>Builds the block validator from the transaction, header and uncles validators.</summary>
    public Func<ITxValidator, IHeaderValidator, IUnclesValidator, IBlockValidator>? BlockValidatorFactory { get; init; }

    /// <summary>Notified after each transaction, while the world state holds exactly its prefix of the block.</summary>
    public BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? TransactionProcessedEventHandler { get; init; }

    public IBlockValidator BlockValidator => _blockValidator ??= CreateBlockValidator();

    public IBlockProcessor BlockProcessor => _blockProcessor ??= GetProcessor();

    public IWorldState WorldState => _worldState ??= new StatelessExecutingWorldState(
        new WorldState(
            new TrieStoreScopeProvider(
                new RawTrieStore(witness.CreateNodeStorage()), witness.CreateCodeDb(), UnavailableStateHeaderProvider.Instance, logManager
            ),
            logManager
        )
    );

    private StatelessBlockTree BlockTree
    {
        get
        {
            if (_blockTree is null)
            {
                using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
                _blockTree = new StatelessBlockTree(headers);
                _parentHeader = headers.Count > 0 ? headers[^1] : null;
            }

            return _blockTree;
        }
    }

    /// <summary>
    /// Validates <paramref name="suggestedBlock"/> against the witness's last header, its parent, then executes it over
    /// the witness state and validates the result.
    /// </summary>
    public StatelessBlockProcessingResult Process(Block suggestedBlock, bool validateHashes = true)
    {
        _ = BlockTree;
        if (_parentHeader is null || suggestedBlock.Header.ParentHash != _parentHeader.Hash)
        {
            return StatelessBlockProcessingResult.Invalid("Witness is missing the parent header");
        }

        if (!BlockValidator.ValidateSuggestedBlock(suggestedBlock, _parentHeader, out string? error, validateHashes))
        {
            return StatelessBlockProcessingResult.Invalid(error);
        }

        if (!WorldState.TryBeginScope(_parentHeader, out IDisposable? scope))
        {
            return StatelessBlockProcessingResult.Invalid("The witness does not contain the parent state root.");
        }

        using (scope)
        {
            Block processedBlock;
            TxReceipt[] receipts;
            try
            {
                (processedBlock, receipts) = BlockProcessor.ProcessOne(
                    suggestedBlock, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(suggestedBlock.Header));
            }
            catch (InvalidBlockException e)
            {
                return StatelessBlockProcessingResult.Invalid(e.Message);
            }

            return BlockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error)
                ? new StatelessBlockProcessingResult(_parentHeader, processedBlock, receipts, null)
                : StatelessBlockProcessingResult.Invalid(error);
        }
    }

    private IBlockValidator CreateBlockValidator()
    {
        HeaderValidator headerValidator = new(BlockTree, sealValidator, specProvider, logManager);
        ITxValidator txValidator = TxValidator ?? new TxValidator(specProvider.ChainId);
        UnclesValidator unclesValidator = new(BlockTree, headerValidator, logManager);
        return BlockValidatorFactory?.Invoke(txValidator, headerValidator, unclesValidator)
            ?? new BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager);
    }

    private BlockProcessor GetProcessor()
    {
        BlockhashProvider blockhashProvider = new(BlockTree, WorldState, logManager);
        ITransactionProcessor txProcessor = CreateTransactionProcessor(WorldState, blockhashProvider);
        BlockAccessListManager blockAccessListManager = new(
            WorldState,
            logManager,
            new BlocksConfig()
            {
                ParallelExecution = false,
                ParallelExecutionBatchRead = false
            },
            new WithdrawalProcessorFactory(logManager),
            new BalTxProcessorFactory(blockhashProvider, specProvider, logManager,
                codeInfoRepositoryFactory: state => new CacheCodeInfoRepository(state, new EthereumPrecompileProvider(), _codeCache),
                transactionProcessorFactory: TransactionProcessorFactory),
            ExecutionRequestsProcessorFactory
        );
        BlockProcessor.ParallelBlockValidationTransactionsExecutor txExecutor = new(
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                WorldState,
                TransactionProcessedEventHandler
            ),
            WorldState,
            specProvider,
            blockAccessListManager,
            logManager
        );

        return new BlockProcessor(
            specProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            txExecutor,
            WorldState,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, WorldState),
            new BlockhashStore(WorldState),
            logManager,
            new WithdrawalProcessor(WorldState, logManager),
            ExecutionRequestsProcessorFactory.Create(txProcessor),
            blockAccessListManager
        );
    }

    private ITransactionProcessor CreateTransactionProcessor(IWorldState state, IBlockhashProvider blockhashProvider)
    {
        EthereumVirtualMachine virtualMachine = new(blockhashProvider, specProvider, logManager);
        CacheCodeInfoRepository codeInfoRepository = new(state, new EthereumPrecompileProvider(), _codeCache);
        return TransactionProcessorFactory?.Create(BlobBaseFeeCalculator.Instance, specProvider, state, virtualMachine, codeInfoRepository, logManager, parallel: false)
            ?? new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, specProvider, state, virtualMachine, codeInfoRepository, logManager);
    }
}
