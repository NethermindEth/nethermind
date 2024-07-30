// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Optimism.CL;

public class Driver
{
    private readonly ICLConfig _config;
    private readonly IL1Bridge _l1Bridge;

    public Driver(IL1Bridge l1Bridge, ICLConfig config)
    {
        _config = config;
        _l1Bridge = l1Bridge;
    }

    private void Start()
    {
        _l1Bridge.OnNewL1Head += OnNewL1Head;
    }

    private void OnNewL1Head(BlockForRpc block, ulong slotNumber)
    {
        // Filter batch submitter transaction
        foreach (TransactionForRpc transaction in block.Transactions.Cast<TransactionForRpc>())
        {
            if (_config.BatcherInboxAddress == transaction.To && _config.BatcherAddress == transaction.From)
            {
                if (transaction.Type == TxType.Blob)
                {
                    ProcessBlobBatcherTransaction(transaction);
                }
                else
                {
                    ProcessCalldataBatcherTransaction(transaction);
                }
            }
        }
    }

    private void ProcessBlobBatcherTransaction(TransactionForRpc transaction)
    {
        int numberOfBlobs = transaction.BlobVersionedHashes!.Length;
        for (int i = 0; i < numberOfBlobs; ++i)
        {

        }
    }

    private void ProcessCalldataBatcherTransaction(TransactionForRpc transaction)
    {
    }
}
