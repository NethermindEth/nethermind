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
        public Hash256? RootHash { get; set; }

        /// <summary>
        /// Accounts of the storage tries to serve
        /// </summary>
        public IOwnedReadOnlyList<PathWithAccount> Accounts { get; set; } = IOwnedReadOnlyList<PathWithAccount>.Empty;

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueHash256? StartingHash { get; set; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueHash256? LimitHash { get; set; }

        /// <summary>
        /// The storage root the range this one continues was verified against, or <c>null</c> when it continues none.
        /// Snap sync bookkeeping only; not part of the wire format.
        /// </summary>
        public Hash256? PrevRunRoot { get; set; }

        /// <summary>Where the consecutive ranges of the storage downloaded at <see cref="PrevRunRoot"/> begin.</summary>
        public ValueHash256 PrevRunStart { get; set; }

        public StorageRange Copy() => new()
        {
            BlockNumber = BlockNumber,
            RootHash = RootHash,
            Accounts = Accounts.AsSpan().ToPooledList(),
            StartingHash = StartingHash,
            LimitHash = LimitHash,
            PrevRunRoot = PrevRunRoot,
            PrevRunStart = PrevRunStart,
        };

        public override string ToString() => $"StorageRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";

        public void Dispose() => Accounts.Dispose();
    }
}
