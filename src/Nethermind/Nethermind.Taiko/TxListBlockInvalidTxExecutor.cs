using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using System.Diagnostics;
using Nethermind.Taiko.Rpc;

namespace Nethermind.Taiko;

[DebuggerDisplay("TxListBlock {Hash} ({Number})")]
public class TxListBlock(BlockHeader blockHeader, List<KeyValuePair<AddressAsKey, Queue<Transaction>>> txSource, int maxBatchCount = 1, ulong maxBytesPerTxList = 0) :
    Block(blockHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>())
{
    public List<KeyValuePair<AddressAsKey, Queue<Transaction>>> TxSource { get; set; } = txSource;
    public int MaxBatchCount => maxBatchCount;
    public ulong MaxBytesPerTxList => maxBytesPerTxList;



    public List<PreBuiltTxList> Batches { get; private set; } = [];

    private List<Transaction> currentBatch = [];

    public void CommitBatch()
    {
        var list = EncodeAndCompress(currentBatch.ToArray());
        Batches.Add(new PreBuiltTxList(list, (ulong)Header.GasUsed, list.Length));
        currentBatch = [];
        Header.GasUsed = 0;
    }

    TxDecoder _txDecoder = Rlp.GetStreamDecoder<Transaction>() as TxDecoder ?? throw new NullReferenceException(nameof(_txDecoder));

    private byte[] EncodeAndCompress(Transaction[] txs)
    {
        int contentLength = txs.Sum(tx => _txDecoder.GetLength(tx, RlpBehaviors.None));
        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

        rlpStream.StartSequence(contentLength);
        foreach (Transaction tx in txs)
        {
            _txDecoder.Encode(rlpStream, tx);
        }

        using MemoryStream stream = new();
        using ZLibStream compressingStream = new(stream, CompressionMode.Compress, false);
        compressingStream.Write(rlpStream.Data);
        compressingStream.Close();
        return stream.ToArray();
    }

    public bool TryAddToBatch(Transaction tx)
    {
        currentBatch.Add(tx);

        byte[] compressed = EncodeAndCompress(currentBatch.ToArray());

        if ((ulong)compressed.LongLength > MaxBytesPerTxList)
        {
            currentBatch.RemoveAt(currentBatch.Count - 1);
            return false;
        }

        return true;
    }

    public bool IsBatchEmpty => !currentBatch.Any();

    public bool NeedMoreBatches => MaxBatchCount > Batches.Count;


    public override Block Copy(BlockHeader header) => new TxListBlock(header, TxSource, maxBatchCount, maxBytesPerTxList);
}

public class TxListBlockInvalidTxExecutor(ITransactionProcessorAdapter txProcessor, IWorldState worldState) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly IWorldState _worldState = worldState;
    private readonly ITransactionProcessorAdapter _txProcessor = txProcessor;

    public event EventHandler<TxProcessedEventArgs> TransactionProcessed = (s, e) => { };

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
        => ProcessTransactions(block as TxListBlock, processingOptions, receiptsTracer, spec);

    private TxReceipt[] ProcessTransactions(TxListBlock? block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
    {
        if (block is null)
        {
            throw new ArgumentException($"Block paased to {nameof(TxListBlockInvalidTxExecutor)} should be of type {nameof(TxListBlock)}");
        }

        if (block.TxSource.Count is 0)
        {
            return [];
        }

        BlockExecutionContext blkCtx = new(block.Header);

        for (int senderCounter = 0; senderCounter < block.TxSource.Count; senderCounter++)
        {
            for (; block.TxSource[senderCounter].Value.Any();)
            {
                Snapshot snap = _worldState.TakeSnapshot();
                Transaction tx = block.TxSource[senderCounter].Value.Peek();

                if (tx.Type == TxType.Blob)
                {
                    block.TxSource[senderCounter].Value.Clear();
                    // Skip blob transactions
                    continue;
                }

                using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);

                try
                {
                    if (!_txProcessor.Execute(tx, in blkCtx, receiptsTracer))
                    // if the transaction was invalid, we ignore it and continue
                    {
                        block.TxSource[senderCounter].Value.Clear();
                        _worldState.Restore(snap);
                        break;
                    }
                }
                catch
                {
                    // sometimes invalid transactions can throw exceptions because
                    // they are detected later in the processing pipeline
                    block.TxSource[senderCounter].Value.Clear();
                    _worldState.Restore(snap);
                    break;
                }
                // only end the trace if the transaction was successful
                // so that we don't increment the receipt index for failed transactions
                receiptsTracer.EndTxTrace();

                if (block.TryAddToBatch(tx))
                {
                    block.TxSource[senderCounter].Value.Dequeue();
                }
                else
                {
                    if (block.IsBatchEmpty)
                    {
                        block.TxSource[senderCounter].Value.Clear();
                        _worldState.Restore(snap);
                        continue;
                    }
                    else
                    {
                        block.CommitBatch();
                        _worldState.Restore(snap);
                        if (!block.NeedMoreBatches)
                        {
                            return [];
                        }
                        continue;
                    }
                }
            }
        }

        if (!block.IsBatchEmpty)
        {
            block.CommitBatch();
        }

        return [];
    }
}
