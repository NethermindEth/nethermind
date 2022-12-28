// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithAccount
    {
        public PathWithAccount() { }

        public PathWithAccount(Keccak path, Account account)
        {
            Path = path;
            Account = account;
        }

        public Keccak Path { get; set; }
        public Account Account { get; set; }
    }
}
