// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Synchronization.Test.FastSync;

public interface IStateSyncTestOperation
{
    Hash256 RootHash { get; }
    void UpdateRootHash();
    void SetAccountsAndCommit(params (Hash256 Address, Account? Account)[] accounts);
    void AssertFlushed();
    void CompareTrees(RemoteDbContext remote, ILogger logger, string stage, bool skipLogs = false);
    void DeleteStateRoot();
}
