// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class Driver : IDisposable
{
    private readonly ICLConfig _config;
    private readonly IL1Bridge _l1Bridge;
    private readonly ILogger _logger;
    private readonly CLChainSpecEngineParameters _engineParameters;
    private readonly IDerivationPipeline _derivationPipeline;
    private readonly ISystemConfigDeriver _systemConfigDeriver;
    private readonly IL2BlockTree _l2BlockTree;
    private readonly IEthRpcModule _l2EthRpc;
    private readonly IDerivedBlocksVerifier _derivedBlocksVerifier;
    private readonly IDecodingPipeline _decodingPipeline;

    private readonly Task _mainTask;
    private readonly ChannelReader<(L1Block, ReceiptForRpc[])> _newL1HeadReader;

    public Driver(IL1Bridge l1Bridge, IEthRpcModule l2EthRpc, IL2BlockTree l2BlockTree, ICLConfig config, CLChainSpecEngineParameters engineParameters, ILogger logger)
    {
        _config = config;
        _l1Bridge = l1Bridge;
        _logger = logger;
        _engineParameters = engineParameters;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        PayloadAttributesDeriver payloadAttributesDeriver = new(480, _systemConfigDeriver,
            new DepositTransactionBuilder(480, engineParameters), logger);
        _l2BlockTree = l2BlockTree;
        _l2EthRpc = l2EthRpc;
        _derivedBlocksVerifier = new DerivedBlocksVerifier(logger);
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, _l2BlockTree, _l1Bridge, _logger);
        _newL1HeadReader = _l1Bridge.NewHeadChannel.Reader;
        _decodingPipeline = new DecodingPipeline(logger);

        _mainTask = new(async () =>
        {
            // TODO: cancellation
            while (true)
            {
                if (_derivationPipeline.DerivedPayloadAttributes.TryRead(out var derivedPayloadAttributes))
                {
                    OnL2BlocksDerived(derivedPayloadAttributes);
                }
                else if (_decodingPipeline.DecodedBatchesReader.TryPeek(out var decodedBatch))
                {
                    ulong parentNumber = decodedBatch.RelTimestamp / 2 - 1;
                    // TODO: make it properly
                    L2Block? l2Parent = _l2BlockTree.GetBlockByNumber(parentNumber);
                    if (l2Parent is not null)
                    {
                        await _derivationPipeline.BatchesForProcessing.WriteAsync((l2Parent, decodedBatch));
                        await _decodingPipeline.DecodedBatchesReader.ReadAsync();
                    }
                    else
                    {
                        if (_l2BlockTree.HeadBlockNumber > parentNumber)
                        {
                            _logger.Error($"Old batch. Skipping");
                            await _decodingPipeline.DecodedBatchesReader.ReadAsync();
                        }
                    }
                }
                if (_newL1HeadReader.TryRead(out (L1Block Block, ReceiptForRpc[]) newHead))
                {
                    await OnNewL1Head(newHead.Block);
                }
            }
        });
    }

    public void Start()
    {
        _decodingPipeline.Start();
        _derivationPipeline.Start();
        _mainTask.Start();
    }

    private void OnL2BlocksDerived(PayloadAttributesRef payloadAttributes)
    {
        var blockResult = _l2EthRpc.eth_getBlockByNumber(new((long)payloadAttributes.Number), true);

        if (blockResult.Result != Result.Success)
        {
            _logger.Error($"Unable to get block by number: {(long)payloadAttributes.Number}");
            return;
        }

        var block = blockResult.Data;
        var expectedPayloadAttributes = PayloadAttributesFromBlockForRpc(block);

        bool success = _l2BlockTree.TryAddBlock(new L2Block()
        {
            Hash = block.Hash,
            Number = (ulong)block.Number!.Value,
            PayloadAttributes = expectedPayloadAttributes,
            ParentHash = block.ParentHash,
            SystemConfig = payloadAttributes.SystemConfig,
            L1BlockInfo = payloadAttributes.L1BlockInfo,
        });

        if (!success)
        {
            _logger.Error($"Unable to put block into block tree");
            return;
        }
        else
        {
            _logger.Error($"Adding block into l2blockTree: {block.Hash}");
        }

        _derivedBlocksVerifier.ComparePayloadAttributes(expectedPayloadAttributes,
            payloadAttributes.PayloadAttributes, payloadAttributes.Number);
    }

    private OptimismPayloadAttributes PayloadAttributesFromBlockForRpc(BlockForRpc block)
    {
        ArgumentNullException.ThrowIfNull(block);
        OptimismPayloadAttributes result = new()
        {
            NoTxPool = true,
            EIP1559Params = null,
            GasLimit = block.GasLimit,
            ParentBeaconBlockRoot = block.ParentBeaconBlockRoot,
            PrevRandao = block.MixHash,
            SuggestedFeeRecipient = block.Miner,
            Timestamp = block.Timestamp.ToUInt64(null),
            Withdrawals = block.Withdrawals?.ToArray()
        };
        Transaction[] txs = block.Transactions.Cast<TransactionForRpc>().Select(t => t.ToTransaction()).ToArray();
        result.SetTransactions(txs);

        return result;
    }

    private ulong CalculateSlotNumber(ulong timestamp)
    {
        // TODO: review
        const ulong beaconGenesisTimestamp = 1606824023;
        const ulong l1SlotTime = 12;
        return (timestamp - beaconGenesisTimestamp) / l1SlotTime;
    }

    private async Task OnNewL1Head(L1Block block)
    {
        _logger.Error($"New L1 Block. Number {block.Number}");
        int startingBlobIndex = 0;
        // Filter batch submitter transaction
        foreach (L1Transaction transaction in block.Transactions!)
        {
            if (transaction.Type == TxType.Blob)
            {
                if (_engineParameters.BatcherInboxAddress == transaction.To &&
                    _engineParameters.BatcherAddress == transaction.From)
                {
                    await ProcessBlobBatcherTransaction(transaction,
                        startingBlobIndex, CalculateSlotNumber(block.Timestamp.ToUInt64(null)));
                }
                startingBlobIndex += transaction.BlobVersionedHashes!.Length;
            }
            else
            {
                if (_engineParameters.BatcherInboxAddress == transaction.To &&
                    _engineParameters.BatcherAddress == transaction.From)
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
    }

    private async Task ProcessBlobBatcherTransaction(L1Transaction transaction, int startingBlobIndex, ulong slotNumber)
    {
        BlobSidecar[]? blobSidecars = await _l1Bridge.GetBlobSidecars(slotNumber, startingBlobIndex,
            startingBlobIndex + transaction.BlobVersionedHashes!.Length);
        while (blobSidecars is null)
        {
            blobSidecars = await _l1Bridge.GetBlobSidecars(slotNumber, startingBlobIndex,
                startingBlobIndex + transaction.BlobVersionedHashes.Length);
        }

        for (int i = 0; i < transaction.BlobVersionedHashes.Length; i++)
        {
            for (int j = 0; j < blobSidecars.Length; ++j)
            {
                if (blobSidecars[j].BlobVersionedHash.SequenceEqual(transaction.BlobVersionedHashes[i]))
                {
                    await _decodingPipeline.DaDataWriter.WriteAsync(blobSidecars[j].Blob);
                }
            }
        }
    }

    private void ProcessCalldataBatcherTransaction(L1Transaction transaction)
    {
        if (_logger.IsError)
        {
            _logger.Error($"GOT REGULAR TRANSACTION");
        }

        throw new NotImplementedException();
    }

    public void Dispose()
    {
    }
}
