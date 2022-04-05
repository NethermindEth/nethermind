using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.State.Snap
{
    public class ProgressTracker
    {
        private const int STORAGE_BATCH_SIZE = 2000;
        private const int CODES_BATCH_SIZE = 400;

        private AccountRange AccountRangeRequested;

        private readonly ILogger _logger;

        public Keccak NextAccountPath { get; set; } = Keccak.Zero;
        //public Keccak NextAccountPath { get; set; } = new("0xfe00000000000000000000000000000000000000000000000000000000000000");
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<Keccak> CodesToRetrieve { get; set; } = new();

        public bool MoreAccountsToRight { get; set; } = true;

        public ProgressTracker(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public (AccountRange accountRange, StorageRange storageRange, Keccak[] codeHashes) GetNextRequest(long blockNumber, Keccak rootHash)
        {
            if (NextSlotRange.TryDequeue(out StorageRange storageRange))
            {
                storageRange.RootHash = rootHash;
                storageRange.BlockNumber = blockNumber;

                LogRequest("NextSlotRange");

                return (null, storageRange, null);
            }
            else if (StoragesToRetrieve.Count > 0)
            {
                // TODO: optimize this
                List<PathWithAccount> storagesToQuery = new(STORAGE_BATCH_SIZE);

                for (int i = 0; i < STORAGE_BATCH_SIZE && StoragesToRetrieve.TryDequeue(out PathWithAccount storage); i++)
                {
                    storagesToQuery.Add(storage);
                }

                StorageRange storageRange2 = new()
                {
                    RootHash = rootHash,
                    Accounts = storagesToQuery.ToArray(),
                    StartingHash = Keccak.Zero,
                    BlockNumber = blockNumber
                };

                LogRequest("StoragesToRetrieve");

                return (null, storageRange2, null);
            }
            else if (CodesToRetrieve.Count > 0)
            {
                // TODO: optimize this
                List<Keccak> codesToQuery = new(CODES_BATCH_SIZE);

                for (int i = 0; i < CODES_BATCH_SIZE && CodesToRetrieve.TryDequeue(out Keccak codeHash); i++)
                {
                    codesToQuery.Add(codeHash);
                }

                LogRequest("CodesToRetrieve");

                return (null, null, codesToQuery.ToArray());
            }
            else if (MoreAccountsToRight && AccountRangeRequested is null)
            {
                // some contract hardcoded
                //var path = Keccak.Compute(new Address("0x4c9A3f79801A189D98D3a5A18dD5594220e4d907").Bytes);
                // = new(_bestHeader.StateRoot, path, path, _bestHeader.Number);

                AccountRangeRequested = new(rootHash, NextAccountPath, Keccak.MaxValue, blockNumber);

                LogRequest("AccountRange");

                return (AccountRangeRequested, null, null);
            }

            return (null, null, null);
        }

        private void LogRequest(string reqType)
        {
            _logger.Info($"SNAP - {reqType}:{AccountRangeRequested is not null} | {NextAccountPath} | {NextSlotRange.Count} | {StoragesToRetrieve.Count} | {CodesToRetrieve.Count}");
        }

        public void EnqueueCodeHashes(ICollection<Keccak> codeHashes)
        {
            foreach (var hash in codeHashes)
            {
                CodesToRetrieve.Enqueue(hash);
            }           
        }

        public void EnqueueAccountStorage(PathWithAccount storage)
        {
            StoragesToRetrieve.Enqueue(storage);
        }

        public void EnqueueAccountStorage(StorageRange storageRange)
        {
            NextSlotRange.Enqueue(storageRange);
        }

        public void ReportRequestFinished(AccountRange accountRange)
        {
            AccountRangeRequested = null;
        }

        public bool IsSnapGetRangesFinished()
        {
            return !MoreAccountsToRight && StoragesToRetrieve.Count == 0 && AccountRangeRequested == null && NextSlotRange.Count == 0;
        }
    }
}
