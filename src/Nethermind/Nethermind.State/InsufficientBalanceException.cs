// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Messages;

namespace Nethermind.State
{
    public class InsufficientBalanceException(Address address) : StateException($"{TxErrorMessages.InsufficientFundsForTransfer}: address {address}")
    {
    }
}
