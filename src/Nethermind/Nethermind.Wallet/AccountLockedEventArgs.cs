// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Wallet
{
    public class AccountLockedEventArgs(Address address) : EventArgs
    {
        public Address Address { get; } = address;
    }
}
