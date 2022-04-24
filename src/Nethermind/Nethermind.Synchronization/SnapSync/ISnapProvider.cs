using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public interface ISnapProvider
    {
        (SnapSyncBatch request, bool finished) GetNextRequest();

        bool CanSync();

        void AddAccountRange(AccountRange request, AccountsAndProofs response);
        bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);

        void AddStorageRange(StorageRange request, SlotsAndProofs response);
        bool AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null);

        void AddCodes(Keccak[] requestedHashes, byte[][] codes);

        void RetryRequest(SnapSyncBatch batch);

        bool IsSnapGetRangesFinished();
    }
}
