// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Core;
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

    private void OnNewL1Head(BeaconBlock block, ulong slotNumber)
    {
        _logger.Error("INVOKED");
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
                    ProcessBlobBatcherTransaction(transaction, slotNumber);
                }
                else
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
    }

    private async void ProcessBlobBatcherTransaction(Transaction transaction, ulong slotNumber)
    {
        if (_logger.IsError)
        {
            _logger.Error($"GOT BLOB TRANSACTION To: {transaction.To}, From: {transaction.SenderAddress}");
        }
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
                    FrameDecoder.DecodeFrames(data);
                    // _logger.Error($"DATA: {BitConverter.ToString(data).Replace("-", "")}");
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
