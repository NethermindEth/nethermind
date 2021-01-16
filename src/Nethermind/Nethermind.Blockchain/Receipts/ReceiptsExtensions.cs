//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Receipts
{
    public static class ReceiptsExtensions
    {
        public static TxReceipt ForTransaction(this TxReceipt[] receipts, Keccak txHash)
            => receipts.FirstOrDefault(r => r.TxHash == txHash);
        
        public static void SetSkipStateAndStatusInRlp(this TxReceipt[] receipts, bool value)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                receipts[i].SkipStateAndStatusInRlp = value;
            }
        }
        
        public static Keccak GetReceiptsRoot(this TxReceipt[] txReceipts, IReleaseSpec releaseSpec, Keccak suggestedRoot)
        {
            Keccak SkipStateAndStatusReceiptsRoot()
            {
                txReceipts.SetSkipStateAndStatusInRlp(true);
                try
                {
                    return new ReceiptTrie(releaseSpec, txReceipts).RootHash;
                }
                finally
                {
                    txReceipts.SetSkipStateAndStatusInRlp(false);
                }
            }

            Keccak receiptsRoot = new ReceiptTrie(releaseSpec, txReceipts).RootHash;
            if (!releaseSpec.ValidateReceipts && receiptsRoot != suggestedRoot)
            {
                var skipStateAndStatusReceiptsRoot = SkipStateAndStatusReceiptsRoot();
                if (skipStateAndStatusReceiptsRoot == suggestedRoot)
                {
                    return skipStateAndStatusReceiptsRoot;
                }
            }
            return receiptsRoot;
        }
    }
}
