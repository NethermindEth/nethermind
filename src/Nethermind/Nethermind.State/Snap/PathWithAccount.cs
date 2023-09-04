// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithAccount
    {
        public PathWithAccount() { }

        public PathWithAccount(ValueKeccak path, Account account)
        {
            Path = path;
            Account = account;
        }

        public ValueKeccak Path { get; set; }
        public Account Account { get; set; }
    }
}
