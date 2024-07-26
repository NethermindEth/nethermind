// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxEventArgs : EventArgs
    {
        public Transaction Transaction { get; }

        public TxEventArgs(Transaction transaction)
        {
            Transaction = transaction;
        }
    }
}
