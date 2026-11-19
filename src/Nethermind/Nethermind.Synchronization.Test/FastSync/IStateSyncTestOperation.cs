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

    /// <summary>Asserts that the local tree matches the remote one, at every stage.</summary>
    /// <remarks><paramref name="stage"/> only labels the log and the failure message.</remarks>
    void CompareTrees(RemoteDbContext remote, ILogger logger, string stage);

    /// <summary>Logs both trees and asserts nothing, for points where the local tree is expected to differ.</summary>
    void DumpTrees(RemoteDbContext remote, ILogger logger, string stage);

    void DeleteStateRoot();
}
