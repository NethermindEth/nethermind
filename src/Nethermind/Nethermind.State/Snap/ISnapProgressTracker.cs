using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public interface ISnapProgressTracker
    {
        public Keccak NextAccountPath { get; set; }
        public bool MoreAccountsToRight { get; set; }

        public (AccountRange accountRange, StorageRange storageRange, Keccak[] codeHashes) GetNextRequest();
        public void EnqueueCodeHashes(ICollection<Keccak> codeHashes);
        public void ReportCodeRequestFinished(ICollection<Keccak> codeHashes = null);
        public void EnqueueAccountStorage(PathWithAccount pwa);
        public void ReportFullStorageRequestFinished(PathWithAccount[] storages = null);
        public void EnqueueStorageRange(StorageRange storageRange);
    }
}
