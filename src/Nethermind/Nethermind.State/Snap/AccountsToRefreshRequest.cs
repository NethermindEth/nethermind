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
        public Hash256 RootHash { get; set; } = null!;

        public IOwnedReadOnlyList<AccountWithStorageStartingHash> Paths { get; set; } = null!;

        public override string ToString() => $"AccountsToRefreshRequest: ({RootHash}, {Paths.Count})";

        public void Dispose() => Paths?.Dispose();
    }

    public class AccountWithStorageStartingHash
    {
        public PathWithAccount PathAndAccount { get; set; } = null!;
        public ValueHash256 StorageStartingHash { get; set; }
        public ValueHash256 StorageHashLimit { get; set; }
    }
}
