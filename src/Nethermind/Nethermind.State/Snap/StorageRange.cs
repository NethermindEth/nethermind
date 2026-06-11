// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Snap
{
    public class StorageRange : IDisposable
    {
        public ulong? BlockNumber { get; set; }

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public Hash256 RootHash { get; set; } = null!;

        /// <summary>
        /// Accounts of the storage tries to serve
        /// </summary>
        public IOwnedReadOnlyList<PathWithAccount> Accounts { get; set; } = null!;

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueHash256? StartingHash { get; set; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueHash256? LimitHash { get; set; }

        public StorageRange Copy() => new()
        {
            BlockNumber = BlockNumber,
            RootHash = RootHash,
            Accounts = Accounts.AsSpan().ToPooledList(),
            StartingHash = StartingHash,
            LimitHash = LimitHash,
        };

        public override string ToString() => $"StorageRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";

        public void Dispose() => Accounts?.Dispose();
    }
}
