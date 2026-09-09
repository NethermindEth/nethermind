// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class PathWithAccount : ISnapEntry
    {
        public PathWithAccount() { }

        public PathWithAccount(ValueHash256 path, Account account)
        {
            Path = path;
            Account = account;
        }

        public ValueHash256 Path { get; set; }
        public Account Account { get; set; }

        /// <summary>
        /// Consecutive storage-range responses for this account that carried no slots and no proof. Snap sync
        /// bookkeeping only; not part of the wire format.
        /// </summary>
        public int EmptyStorageResponses;

        public byte[] ToRlpValue() =>
            (Account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(Account)).Bytes;
    }
}
