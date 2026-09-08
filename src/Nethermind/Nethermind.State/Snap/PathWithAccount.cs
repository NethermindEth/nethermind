// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class PathWithAccount : ISnapEntry
    {
        public PathWithAccount() { }

        public PathWithAccount(ValueHash256 path, Account? account)
        {
            Path = path;
            Account = account;
        }

        public ValueHash256 Path { get; set; }
        public Account? Account { get; set; }

        public byte[] ToRlpValue()
        {
            Account account = Account ?? throw new InvalidOperationException("An account value is required when encoding a SNAP trie entry.");
            return (account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(account)).Bytes;
        }
    }
}
