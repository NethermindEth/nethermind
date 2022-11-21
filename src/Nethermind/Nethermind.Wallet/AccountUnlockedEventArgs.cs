// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Wallet
{
    public class AccountUnlockedEventArgs : EventArgs
    {
        public Address Address { get; }

        public AccountUnlockedEventArgs(Address address)
        {
            Address = address;
        }
    }
}
