// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Transactions
{
    public static class TxSourceExtensions
    {
        public static ITxSource Then(this ITxSource? txSource, ITxSource? secondTxSource)
        {
            if (secondTxSource is null)
            {
                return txSource ?? EmptyTxSource.Instance;
            }

            if (txSource is null)
            {
                return secondTxSource;
            }

            if (txSource is CompositeTxSource cts)
            {
                cts.Then(secondTxSource);
                return cts;
            }
            else if (secondTxSource is CompositeTxSource cts2)
            {
                cts2.First(secondTxSource);
                return cts2;
            }
            else
            {
                return new CompositeTxSource(txSource, secondTxSource);
            }
        }

        public static ITxSource ServeTxsOneByOne(this ITxSource source) => new OneByOneTxSource(source);
    }
}
