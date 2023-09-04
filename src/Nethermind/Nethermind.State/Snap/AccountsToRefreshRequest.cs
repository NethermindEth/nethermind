// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountsToRefreshRequest
    {
        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public ValueKeccak RootHash { get; set; }

        public AccountWithStorageStartingHash[] Paths { get; set; }

        public override string ToString()
        {
            return $"AccountsToRefreshRequest: ({RootHash}, {Paths.Length})";
        }
    }

    public class AccountWithStorageStartingHash
    {
        public PathWithAccount PathAndAccount { get; set; }
        public ValueKeccak StorageStartingHash { get; set; }
    }
}
