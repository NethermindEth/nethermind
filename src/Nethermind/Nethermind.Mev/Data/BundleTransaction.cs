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
// 

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Mev.Data
{
    public class BundleTransaction : Transaction
    {
        public BundleTransaction(Transaction transaction)
        {
            ChainId = transaction.ChainId;
            Type = transaction.Type;
            Nonce = transaction.Nonce;
            GasPrice = transaction.GasPrice;
            GasBottleneck = transaction.GasBottleneck;
            DecodedMaxFeePerGas = transaction.DecodedMaxFeePerGas;
            GasLimit = transaction.GasLimit;
            To = transaction.To;
            Value = transaction.Value;
            Data = transaction.Data;
            SenderAddress = transaction.SenderAddress;
            Signature = transaction.Signature;
            Hash = transaction.Hash;
            DeliveredBy = transaction.DeliveredBy;
            Timestamp = transaction.Timestamp;
            AccessList = transaction.AccessList;
            IsServiceTransaction = transaction.IsServiceTransaction;
            PoolIndex = transaction.PoolIndex;
        }

        public static BundleTransaction[] ConvertTransactionArray(Transaction[] txs)
        {
            return txs.Select(tx => new BundleTransaction(tx)).ToArray();
        }
        
        public Keccak BundleHash { get; set; } = Keccak.Zero;
        public bool CanRevert { get; set; } = false;
    }
}
