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

namespace Nethermind.Core.Test.Builders
{
    public class TransactionBuilder : BuilderBase<Transaction>
    {   
        public TransactionBuilder()
        {   
            TestObject = new Transaction();
            TestObject.GasPrice = 1;
            TestObject.GasLimit = 21000;
            TestObject.To = Address.Zero;
            TestObject.Nonce = 0;
            TestObject.Value = 1;
            TestObject.Data = new byte[0];
        }

        public TransactionBuilder Signed(IEthereumSigner signer, PrivateKey privateKey)
        {
            signer.Sign(privateKey, TestObject);
            TestObject.Hash = Transaction.CalculateHash(TestObject);
            return this;
        }
    }
}