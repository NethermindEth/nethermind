// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State
{
    public class InsufficientBalanceException : StateException
    {
        public InsufficientBalanceException(Address address)
            : base($"insufficient funds for transfer: address {address}")
        { }
    }
}
