// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Wallet
{
    public class AccountLockedEventArgs : EventArgs
    {
        public Address Address { get; }

        public AccountLockedEventArgs(Address address)
        {
            Address = address;
        }
    }
}
