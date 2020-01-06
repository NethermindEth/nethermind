﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Containers
{
    public class DepositData
    {
        public DepositData(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Gwei amount)
            : this(publicKey, withdrawalCredentials, amount, BlsSignature.Empty)
        {
        }

        public DepositData(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Gwei amount, BlsSignature signature)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            Amount = amount;
            Signature = signature;
        }

        public Gwei Amount { get; }

        public BlsPublicKey PublicKey { get; }

        public BlsSignature Signature { get; private set; }

        public Hash32 WithdrawalCredentials { get; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"P:{PublicKey.ToString().Substring(0, 12)} A:{Amount}";
        }
    }
}
