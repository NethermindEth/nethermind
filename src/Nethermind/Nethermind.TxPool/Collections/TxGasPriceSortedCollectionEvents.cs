// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool.Collections
{
    public partial class TxGasPriceSortedCollection
    {
#pragma warning disable 67
        public event EventHandler<TxGasPriceSortedCollectionEventArgs>? Inserted;
        public event EventHandler<TxGasPriceSortedCollectionRemovedEventArgs>? Removed;
#pragma warning restore 67

        public class TxGasPriceSortedCollectionEventArgs
        {
            public ValueHash256 Hash { get; }
            public UInt256 GasPrice { get; }

            public TxGasPriceSortedCollectionEventArgs(ValueHash256 hash, UInt256 gasPrice)
            {
                Hash = hash;
                GasPrice = gasPrice;
            }
        }

        public class TxGasPriceSortedCollectionRemovedEventArgs : TxGasPriceSortedCollectionEventArgs
        {
            public bool Evicted { get; }

            public TxGasPriceSortedCollectionRemovedEventArgs(ValueHash256 hash, UInt256 gasPrice, bool evicted) : base(hash, gasPrice)
            {
                Evicted = evicted;
            }
        }
    }

}