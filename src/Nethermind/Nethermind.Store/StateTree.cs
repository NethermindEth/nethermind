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

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public class StateTree : PatriciaTree
    {
        private readonly AccountDecoder _decoder = new AccountDecoder();
        
        [DebuggerStepThrough]
        public StateTree() : base(new MemDb(), Keccak.EmptyTreeHash, true)
        {
        }
        
        [DebuggerStepThrough]
        public StateTree(IDb db) : base(db, Keccak.EmptyTreeHash, true)
        {
        }

        [DebuggerStepThrough]
        public StateTree(IDb db, Keccak rootHash) : base(db, rootHash, true)
        {
        }

        [DebuggerStepThrough]
        public Account Get(Address address, Keccak rootHash = null)
        {
            byte[] bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            if (bytes == null)
            {
                return null;
            }

            return _decoder.Decode(bytes.AsRlpStream());
        }
        
        [DebuggerStepThrough]
        internal Account Get(Keccak keccak) // for testing
        {
            byte[] bytes = Get(keccak.Bytes);
            if (bytes == null)
            {
                return null;
            }

            return _decoder.Decode(bytes.AsRlpStream());
        }

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        public void Set(Address address, Account account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Set(keccak.BytesAsSpan, account == null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }
        
        [DebuggerStepThrough]
        internal void Set(Keccak keccak, Account account) // for testing
        {
            Set(keccak.Bytes, account == null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }
    }
}