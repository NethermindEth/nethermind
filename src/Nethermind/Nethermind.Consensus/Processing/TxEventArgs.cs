// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class TxEventArgs : EventArgs
    {
        public int Index { get; }
        public Transaction Transaction { get; }

        public TxEventArgs(int index, Transaction transaction)
        {
            Index = index;
            Transaction = transaction;
        }
    }
}
