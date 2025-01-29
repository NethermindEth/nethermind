// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
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
        // TODO: put into batch queue
    }

    public async void OnNewL1Head(BeaconBlock block, ReceiptForRpc[] receipts)
    {
        _logger.Error($"INVOKED {block.SlotNumber}");
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
        _logger.Error("SLOT PROCESSED");
    }

    private async Task ProcessBlobBatcherTransaction(Transaction transaction, ulong slotNumber)
    {
        _logger.Error($"PROCESS BLOB");
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
                    _logger.Error($"GOT BLOB VERSIONED HASH: {BitConverter.ToString(transaction.BlobVersionedHashes[i]!).Replace("-", "")}");
                    _logger.Error($"BLOB: {BitConverter.ToString(blobSidecars[j].Blob[..32]).Replace("-", "")}");
                    byte[] data = BlobDecoder.DecodeBlob(blobSidecars[j]);
                    Frame[] frames = FrameDecoder.DecodeFrames(data);
                    _logger.Error($"FRAME: {frames[0].FrameNumber} {frames[0].IsLast} {frames[0].ChannelId}");
                    _channelStorage.ConsumeFrames(frames);
                }
            }
        }
    }

    private void InsertBlock()
    {
        var block = _l2EthRpc.eth_getBlockByNumber(new(9176832), true).Data;
        OptimismTransactionForRpc tx = (OptimismTransactionForRpc)block.Transactions.First();
        SystemConfig config =
            _systemConfigDeriver.SystemConfigFromL2BlockInfo(tx.Input!, block.ExtraData, (ulong)block.GasLimit);
        L2Block nativeBlock = new()
        {
            Hash = block.Hash,
            Number = (ulong)block.Number!.Value,
            Timestamp = block.Timestamp.ToUInt64(null),
            ParentHash = block.ParentHash,
            SystemConfig = config,
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
