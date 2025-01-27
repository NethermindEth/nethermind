// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class Driver : IDisposable
{
    private readonly ICLConfig _config;
    private readonly IL1Bridge _l1Bridge;
    private readonly ILogger _logger;
    private readonly CLChainSpecEngineParameters _engineParameters;

    private readonly IChannelStorage _channelStorage;

    public Driver(IL1Bridge l1Bridge, ICLConfig config, CLChainSpecEngineParameters engineParameters, ILogger logger)
    {
        _config = config;
        _l1Bridge = l1Bridge;
        _logger = logger;
        _channelStorage = new ChannelStorage();
        _engineParameters = engineParameters;
    }

    public void Start()
    {
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
