// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class TxEventArgs(int index, Transaction transaction) : EventArgs
    {
        public int Index { get; private protected set; } = index;
        public Transaction Transaction { get; private protected set; } = transaction;
    }
}
