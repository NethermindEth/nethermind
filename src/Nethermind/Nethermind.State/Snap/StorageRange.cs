// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Snap
{
    public class StorageRange : IDisposable
    {
        public long? BlockNumber { get; set; }

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public ValueHash256 RootHash { get; set; }

        /// <summary>
        /// Accounts of the storage tries to serve
        /// </summary>
        public IOwnedReadOnlyList<PathWithAccount> Accounts { get; set; }

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueHash256? StartingHash { get; set; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueHash256? LimitHash { get; set; }

        public StorageRange Copy()
        {
            return new StorageRange()
            {
                BlockNumber = BlockNumber,
                RootHash = RootHash,
                Accounts = Accounts.ToPooledList(Accounts.Count),
                StartingHash = StartingHash,
                LimitHash = LimitHash,
            };
        }

        public override string ToString()
        {
            return $"StorageRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";
        }

        public void Dispose()
        {
            Accounts?.Dispose();
        }
    }
}
