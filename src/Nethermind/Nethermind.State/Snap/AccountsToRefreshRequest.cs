// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountsToRefreshRequest : IDisposable
    {
        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public ValueHash256 RootHash { get; set; }

        public IOwnedReadOnlyList<AccountWithStorageStartingHash> Paths { get; set; }

        public override string ToString()
        {
            return $"AccountsToRefreshRequest: ({RootHash}, {Paths.Count})";
        }

        public void Dispose()
        {
            Paths?.Dispose();
        }
    }

    public class AccountWithStorageStartingHash
    {
        public PathWithAccount PathAndAccount { get; set; }
        public ValueHash256 StorageStartingHash { get; set; }
    }
}
