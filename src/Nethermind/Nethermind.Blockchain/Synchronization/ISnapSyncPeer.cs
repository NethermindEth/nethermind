using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.State.Snap;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISnapSyncPeer
    {
        Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token);
        Task<SlotsAndProofs> GetStoragetRange(StorageRange range, CancellationToken token);
    }
}
