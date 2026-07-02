// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    /// <summary>
    /// Column families of the partial archive database (see <see cref="DbNames.PartialArchive"/>),
    /// which tracks superseded trie-node keys so a rolling window of historical state can be
    /// retained and pruned.
    /// </summary>
    public enum PartialArchiveColumns
    {
        /// <summary>Per trie path: keccak and block number of the latest persisted node version.</summary>
        LatestVersion,

        /// <summary>Per node key: the block of its most recent supersession. Re-created versions move forward here, invalidating older expiry rows.</summary>
        SupersededAt,

        /// <summary>Block-ordered index of supersessions: node keys deletable once their (latest) supersession block leaves the retention window.</summary>
        ExpiryJournal,

        /// <summary>Bookkeeping: retention floor, last persisted snapshot block.</summary>
        Metadata,
    }
}
