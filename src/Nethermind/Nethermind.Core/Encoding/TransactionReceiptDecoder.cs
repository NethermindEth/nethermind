/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class TransactionReceiptDecoder : IRlpDecoder<TransactionReceipt>
    {
        public TransactionReceipt Decode(DecodedRlp rlp)
        {
            TransactionReceipt receipt = new TransactionReceipt();
            byte[] firstItem = rlp.GetBytes(0);
            if (firstItem.Length == 1)
            {
                receipt.StatusCode = firstItem[0];
            }
            else
            {
                receipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            receipt.GasUsed = rlp.GetInt(1);
            receipt.Bloom = new Bloom(rlp.GetBytes(2).ToBigEndianBitArray2048());
            receipt.Logs = rlp.GetComplexObjectArray<LogEntry>(3);

            return receipt;
        }
    }
}