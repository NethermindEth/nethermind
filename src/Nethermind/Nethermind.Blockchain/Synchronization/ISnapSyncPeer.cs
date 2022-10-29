using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISnapSyncPeer
    {
        Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token);
        Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token);
        Task<IList<byte[]>> GetByteCodes(Keccak[] codeHashes, CancellationToken token);
        Task<IList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token);
        Task<IList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token);
    }
}
