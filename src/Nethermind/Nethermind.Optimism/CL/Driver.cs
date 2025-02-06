// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
using Nethermind.Serialization.Rlp;

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

    public Driver(IL1Bridge l1Bridge, IEthRpcModule l2EthRpc, ICLConfig config, CLChainSpecEngineParameters engineParameters, ILogger logger)
    {
        _config = config;
        _l1Bridge = l1Bridge;
        _logger = logger;
        _channelStorage = new ChannelStorage();
        _engineParameters = engineParameters;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        PayloadAttributesDeriver payloadAttributesDeriver = new(480, _systemConfigDeriver,
            new DepositTransactionBuilder(480, engineParameters), logger);
        _l2BlockTree = new L2BlockTree();
        _l2EthRpc = l2EthRpc;
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, _l2BlockTree, _l1Bridge, _logger);
    }

    public void Start()
    {
        InsertBlock();
        _channelStorage.OnChannelBuilt += OnChannelBuilt;
        _derivationPipeline.OnL2BlocksDerived += OnL2BlocksDerived;
        // _l1Bridge.OnNewL1Head += OnNewL1Head;
    }

    private void OnChannelBuilt(byte[] channelData)
    {
        byte[] decompressed = ChannelDecoder.DecodeChannel(channelData);
        // TODO: avoid rlpStream here
        RlpStream rlpStream = new(decompressed);
        ReadOnlySpan<byte> batchData = rlpStream.DecodeByteArray();
        BatchV1[] batches = BatchDecoder.Instance.DecodeSpanBatches(ref batchData);
        _derivationPipeline.ConsumeV1Batches(batches);
    }

    private void OnL2BlocksDerived(OptimismPayloadAttributes[] payloadAttributes, ulong l2ParentNumber)
    {
        _logger.Error($"CHECKING PAYLOAD ATTRIBUTES");
        for (int i = 0; i < 1; i++)
        {
            var block = _l2EthRpc.eth_getBlockByNumber(new((long)l2ParentNumber + 1 + i), true).Data;
            var expectedPayloadAttributes = PayloadAttributesFromBlockForRpc(block);

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
            _logger.Error($"Invalid Transactions.Length");
        }
        //
        for (int i = 0; i < expected.Transactions.Length; i++)
        {
            if (!expected.Transactions[i].SequenceEqual(actual.Transactions[i]))
            {
                // _logger.Error($"HERE");
                // Transaction expectedTransaction =
                //     decoder.Decode(expected.Transactions[i], new(expected.Transactions[i]), RlpBehaviors.SkipTypedWrapping)!;
                //
                // _logger.Error($"HERE2");
                // Transaction actualTransaction =
                //     decoder.Decode(actual.Transactions[i], new(actual.Transactions[i]), RlpBehaviors.SkipTypedWrapping)!;;
                _logger.Error($"Invalid Transaction {i}. Expected:\n{
                    BitConverter.ToString(expected.Transactions[i]).ToLower().Replace("-", "")}, Actual:\n{
                        BitConverter.ToString(actual.Transactions[i]).ToLower().Replace("-", "")}");
                // _logger.Error($"Invalid Transaction. Expected:\n{expectedTransaction}, Actual:\n{actualTransaction}");
            }
        }
        _logger.Error($"CHECKED");
    }

    public async void OnNewL1Head(BeaconBlock block, ReceiptForRpc[] receipts)
    {
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
                }
            }
        }
    }

    private void InsertBlock()
    {
        var block = _l2EthRpc.eth_getBlockByNumber(new(9176832), true).Data;
        DepositTransactionForRpc tx = (DepositTransactionForRpc)block.Transactions.First();
        SystemConfig config =
            _systemConfigDeriver.SystemConfigFromL2BlockInfo(tx.Input!, block.ExtraData, (ulong)block.GasLimit);
        L2Block nativeBlock = new()
        {
            Hash = block.Hash,
            Number = (ulong)block.Number!.Value,
            Timestamp = block.Timestamp.ToUInt64(null),
            ParentHash = block.ParentHash,
            SystemConfig = config,
            L1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(tx.Input!, block.ExtraData),
        };
        _l2BlockTree.AddBlock(nativeBlock);
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
        _l1Bridge.OnNewL1Head -= OnNewL1Head;
    }
}
