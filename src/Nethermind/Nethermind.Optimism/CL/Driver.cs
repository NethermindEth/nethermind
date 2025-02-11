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

    private readonly IChannelStorage _channelStorage;

    private readonly Task _mainTask;
    private readonly ChannelReader<(BeaconBlock, ReceiptForRpc[])> _newL1HeadReader;

    public Driver(IL1Bridge l1Bridge, IEthRpcModule l2EthRpc, IL2BlockTree l2BlockTree, ICLConfig config, CLChainSpecEngineParameters engineParameters, ILogger logger)
    {
        _config = config;
        _l1Bridge = l1Bridge;
        _logger = logger;
        _channelStorage = new ChannelStorage();
        _engineParameters = engineParameters;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        PayloadAttributesDeriver payloadAttributesDeriver = new(480, _systemConfigDeriver,
            new DepositTransactionBuilder(480, engineParameters), logger);
        _l2BlockTree = l2BlockTree;
        _l2EthRpc = l2EthRpc;
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, _l2BlockTree, _l1Bridge, _logger);
        _newL1HeadReader = _l1Bridge.NewHeadChannel.Reader;

        _mainTask = new(async () =>
        {
            // TODO: cancelation
            while (true)
            {
                var l1Block = await _newL1HeadReader.ReadAsync();
                await OnNewL1Head(l1Block.Item1, l1Block.Item2);
            }
        });
    }

    public void Start()
    {
        _mainTask.Start();
    }

    private void OnL2BlocksDerived(OptimismPayloadAttributes[] payloadAttributes, SystemConfig[] systemConfigs, L1BlockInfo[] l1BlockInfos, ulong l2ParentNumber)
    {
        _logger.Error($"CHECKING PAYLOAD ATTRIBUTES {payloadAttributes.Length}");
        for (int i = 0; i < payloadAttributes.Length; i++)
        {
            var blockResult = _l2EthRpc.eth_getBlockByNumber(new((long)l2ParentNumber + 1 + i), true);

            if (blockResult.Result != Result.Success)
            {
                _logger.Error($"Unable to get block by number: {(long)l2ParentNumber + i + 1}");
                continue;
            }
            var block = blockResult.Data;
            var expectedPayloadAttributes = PayloadAttributesFromBlockForRpc(block);

            bool success = _l2BlockTree.TryAddBlock(new L2Block()
            {
                Hash = block.Hash,
                Number = (ulong)block.Number!.Value,
                Timestamp = (ulong)block.Timestamp,
                ParentHash = block.ParentHash,
                SystemConfig = systemConfigs[i],
                L1BlockInfo = l1BlockInfos[i],
            });

            if (!success)
            {
                _logger.Error($"Unable to put block into block tree");
                continue;
            }
            else
            {
                _logger.Error($"Adding block into l2blockTree: {block.Hash}");
            }

            ComparePayloadAttributes(expectedPayloadAttributes, payloadAttributes[i]);
        }
    }

    private OptimismPayloadAttributes PayloadAttributesFromBlockForRpc(BlockForRpc block)
    {
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

    private void ComparePayloadAttributes(OptimismPayloadAttributes expected, OptimismPayloadAttributes actual)
    {
        if (expected.NoTxPool != actual.NoTxPool)
        {
            _logger.Error($"Invalid NoTxPool. Expected {expected.NoTxPool}, Actual {actual.NoTxPool}");
        }

        if (expected.EIP1559Params != actual.EIP1559Params)
        {
            _logger.Error($"Invalid Eip1559Params");
        }

        if (expected.GasLimit != actual.GasLimit)
        {
            _logger.Error($"Invalid GasLimit. Expected {expected.GasLimit}, Actual {actual.GasLimit}");
        }

        if (expected.ParentBeaconBlockRoot != actual.ParentBeaconBlockRoot)
        {
            _logger.Error($"Invalid ParentBeaconBlockRoot. Expected {expected.ParentBeaconBlockRoot}, Actual {actual.ParentBeaconBlockRoot}");
        }

        if (expected.PrevRandao != actual.PrevRandao)
        {
            _logger.Error($"Invalid PrevRandao. Expected {expected.PrevRandao}, Actual {actual.PrevRandao}");
        }

        if (expected.SuggestedFeeRecipient != actual.SuggestedFeeRecipient)
        {
            _logger.Error($"Invalid SuggestedFeeRecipient. Expected {expected.SuggestedFeeRecipient}, Actual {actual.SuggestedFeeRecipient}");
        }

        if (expected.Timestamp != actual.Timestamp)
        {
            _logger.Error($"Invalid Timestamp. Expected {expected.Timestamp}, Actual {actual.Timestamp}");
        }

        if (expected.Withdrawals != actual.Withdrawals)
        {
            _logger.Error($"Invalid Withdrawals");
        }

        if (expected.Transactions!.Length != actual.Transactions!.Length)
        {
            _logger.Error($"Invalid Transactions.Length. Expected {expected.Transactions!.Length}, Actual {actual.Transactions!.Length}");
        }
        _logger.Error($"CHECKED");
    }

    private async Task OnNewL1Head(BeaconBlock block, ReceiptForRpc[] receipts)
    {
        _logger.Error($"New L1 Block. Number {block.PayloadNumber}");
        // Filter batch submitter transaction
        foreach (Transaction transaction in block.Transactions)
        {
            if (_engineParameters.BatcherInboxAddress == transaction.To && _engineParameters.BatcherAddress == transaction.SenderAddress)
            {
                if (transaction.Type == TxType.Blob)
                {
                    await ProcessBlobBatcherTransaction(transaction, block.SlotNumber);
                }
                else
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
    }

    private async Task ProcessBlobBatcherTransaction(Transaction transaction, ulong slotNumber)
    {
        BlobSidecar[]? blobSidecars = await _l1Bridge.GetBlobSidecars(slotNumber);
        while (blobSidecars is null)
        {
            await Task.Delay(100);
            blobSidecars = await _l1Bridge.GetBlobSidecars(slotNumber);
        }

        for (int i = 0; i < transaction.BlobVersionedHashes!.Length; i++)
        {
            for (int j = 0; j < blobSidecars.Length; ++j)
            {
                if (blobSidecars[j].BlobVersionedHash.SequenceEqual(transaction.BlobVersionedHashes[i]!))
                {
                    byte[] data = BlobDecoder.DecodeBlob(blobSidecars[j]);
                    Frame[] frames = FrameDecoder.DecodeFrames(data);
                    _channelStorage.ConsumeFrames(frames);
                    BatchV1[]? batches = _channelStorage.GetReadyBatches();
                    if (batches is not null)
                    {
                        L2Block? l2Parent = _l2BlockTree.GetHighestBlock();
                        ArgumentNullException.ThrowIfNull(l2Parent);
                        var result = await _derivationPipeline.ConsumeV1Batches(l2Parent, batches);
                        OnL2BlocksDerived(result.Item1, result.Item2, result.Item3, l2Parent.Number);
                    }
                }
            }
        }
    }

    private void ProcessCalldataBatcherTransaction(Transaction transaction)
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
