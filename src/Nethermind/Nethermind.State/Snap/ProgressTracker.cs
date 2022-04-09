using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Snap
{
    public class ProgressTracker
    {
        long _testReqCount;

        private const int STORAGE_BATCH_SIZE = 2000;
        private const int CODES_BATCH_SIZE = 400;
        private readonly byte[] ACC_PROGRESS_KEY = Encoding.ASCII.GetBytes("AccountProgressKey");

        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private int _activeCodeRequests;

        private readonly ILogger _logger;
        private readonly IDb _db;

        public Keccak NextAccountPath { get; set; } = Keccak.Zero;
        //public Keccak NextAccountPath { get; set; } = new("0xffe0000000000000000000000000000000000000000000000000000000000000");
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<Keccak> CodesToRetrieve { get; set; } = new();

        public bool MoreAccountsToRight { get; set; } = true;

        public ProgressTracker(IDb db, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            byte[] progress = _db.Get(ACC_PROGRESS_KEY);
            if(progress != null)
            {
                NextAccountPath = new Keccak(progress);
                _logger.Info($"SNAP - State Ranges (Phase 1) progress loaded from DB:{NextAccountPath}");

                if (NextAccountPath == Keccak.MaxValue)
                {
                    _logger.Info($"SNAP - State Ranges (Phase 1) is finished. Healing (Phase 2) starting...");
                    MoreAccountsToRight = false;
                }
            }
        }

        public (AccountRange accountRange, StorageRange storageRange, Keccak[] codeHashes) GetNextRequest(long blockNumber, Keccak rootHash)
        {
            if (NextSlotRange.TryDequeue(out StorageRange storageRange))
            {
                storageRange.RootHash = rootHash;
                storageRange.BlockNumber = blockNumber;

                LogRequest("NextSlotRange");

                Interlocked.Increment(ref _activeStorageRequests);

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

                Interlocked.Increment(ref _activeStorageRequests);

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

                Interlocked.Increment(ref _activeCodeRequests);

                return (null, null, codesToQuery.ToArray());
            }
            else if (MoreAccountsToRight && _activeAccountRequests == 0)
            {
                // some contract hardcoded
                //var path = Keccak.Compute(new Address("0x4c9A3f79801A189D98D3a5A18dD5594220e4d907").Bytes);
                // = new(_bestHeader.StateRoot, path, path, _bestHeader.Number);

                AccountRange range = new(rootHash, NextAccountPath, Keccak.MaxValue, blockNumber);

                LogRequest("AccountRange");

                Interlocked.Increment(ref _activeAccountRequests);

                return (range, null, null);
            }

            return (null, null, null);
        }

        private void LogRequest(string reqType)
        {
            _testReqCount++;

            if (_testReqCount % 1 == 0)
            {
                _logger.Info($"SNAP - ({reqType}) AccountRequests:{_activeAccountRequests} | StorageRequests:{_activeStorageRequests} | CodeRequests:{_activeCodeRequests} | {NextAccountPath} | {NextSlotRange.Count} | {StoragesToRetrieve.Count} | {CodesToRetrieve.Count}");
            }
        }

        internal void EnqueueCodeHashes(ICollection<Keccak> codeHashes)
        {
            if (codeHashes is not null)
            {
                foreach (var hash in codeHashes)
                {
                    CodesToRetrieve.Enqueue(hash);
                }
            }
        }

        public void ReportCodeRequestFinished(ICollection<Keccak> codeHashes = null)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
        }

        public void EnqueueAccountStorage(PathWithAccount pwa)
        {
            StoragesToRetrieve.Enqueue(pwa);
        }

        public void ReportFullStorageRequestFinished(PathWithAccount[] storages = null)
        {
            if (storages is not null)
            {
                foreach (var s in storages)
                {
                    EnqueueAccountStorage(s);
                    
                }              
            }

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void EnqueueStorageRange(StorageRange storageRange)
        {
            if (storageRange is not null)
            {
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void ReportStorageRangeRequestFinished(StorageRange storageRange = null)
        {
            EnqueueStorageRange(storageRange);

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void ReportAccountRequestFinished()
        {
            Interlocked.Decrement(ref _activeAccountRequests);
        }

        public bool IsSnapGetRangesFinished()
        {
            return !MoreAccountsToRight 
                && StoragesToRetrieve.Count == 0 
                && NextSlotRange.Count == 0 
                && CodesToRetrieve.Count == 0
                && _activeAccountRequests == 0
                && _activeStorageRequests == 0
                && _activeCodeRequests == 0;
        }

        public void FinishRangePhase()
        {
            MoreAccountsToRight = false;
            NextAccountPath = Keccak.MaxValue;
            _db.Set(ACC_PROGRESS_KEY, NextAccountPath.Bytes);
        }
    }
}
