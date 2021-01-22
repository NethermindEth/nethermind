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

using System.Diagnostics;

namespace Nethermind.HashLib
{
    internal class HashAlgorithmWrapper : System.Security.Cryptography.HashAlgorithm
    {
        private IHash m_hash;

        public HashAlgorithmWrapper(IHash a_hash)
        {
            m_hash = a_hash;
            HashSizeValue = a_hash.HashSize * 8;
        }

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            Debug.Assert(cbSize >= 0);
            Debug.Assert(ibStart >= 0);
            Debug.Assert(ibStart + cbSize <= array.Length);

            m_hash.TransformBytes(array, ibStart, cbSize);
        }

        protected override byte[] HashFinal()
        {
            HashValue = m_hash.TransformFinal().GetBytes();
            return HashValue;
        }

        public override void Initialize()
        {
            m_hash.Initialize();
        }
    }
}
