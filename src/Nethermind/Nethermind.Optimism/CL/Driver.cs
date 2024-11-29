// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL;

public class Driver
{
    private readonly ICLConfig _config;
    private readonly IL1Bridge _l1Bridge;
    private readonly ILogger _logger;

    public Driver(IL1Bridge l1Bridge, ICLConfig config, ILogger logger)
    {
        _config = config;
        _l1Bridge = l1Bridge;
        _logger = logger;
    }

    public void Start()
    {
        _l1Bridge.OnNewL1Head += OnNewL1Head;
    }

    private void OnNewL1Head(BeaconBlock block, ReceiptForRpc[] receipts)
    {
        _logger.Error($"INVOKED {block.SlotNumber}");
        Address sepoliaBatcher = new("0x8F23BB38F531600e5d8FDDaAEC41F13FaB46E98c");
        Address batcherInboxAddress = new("0xff00000000000000000000000000000011155420");
        // Filter batch submitter transaction
        foreach (Transaction transaction in block.Transactions)
        {
            // _logger.Error($"Tx To: {transaction.To}, From: {transaction.SenderAddress} end");
            if (batcherInboxAddress == transaction.To && sepoliaBatcher == transaction.SenderAddress)
            {
                if (transaction.Type == TxType.Blob)
                {
                    ProcessBlobBatcherTransaction(transaction, block.SlotNumber);
                }
                else
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
        _logger.Error("SLOT PROCESSED");
    }

    private async void ProcessBlobBatcherTransaction(Transaction transaction, ulong slotNumber)
    {
        BlobSidecar[] blobSidecars = await _l1Bridge.GetBlobSidecars(slotNumber);
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
                    _logger.Error($"FRAMES NUMBER: {frames.Length}");
                    foreach (Frame frame in frames)
                    {
                        // _logger.Error($"FRAME DATA: {BitConverter.ToString(frame.FrameData).Replace("-", "").ToLower()}");
                        // await Task.Delay(100);
                        (BatchV1 batch, byte[] _) = ChannelDecoder.DecodeChannel(frame);

                        // BatchV1 batch = BatchDecoder.Instance.DecodeSpanBinary(frame.FrameData);

                        _logger.Error($"BATCH: L1OriginNum: {batch.L1OriginNum} BlockCount: {batch.BlockCount} RelTimestamp: {batch.RelTimestamp} L1OriginCheck: {BitConverter.ToString(batch.L1OriginCheck).Replace("-", "").ToLower()}");
                        int currentTx = 0;
                        foreach (var txCount in batch.BlockTxCounts)
                        {
                            _logger.Error($"L2 BLOCK: txCount: {txCount}");
                            for (int k = 0; k < (int)txCount; ++k)
                            {
                                _logger.Error($"TX: to: {batch.Txs.Tos[currentTx + k]}");
                            }

                            currentTx += (int)txCount;
                        }
                        _logger.Error($"END BATCH");
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
    }
}
