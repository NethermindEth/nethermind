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

using Nethermind.HashLib.Extensions;

namespace Nethermind.HashLib.Crypto
{
    internal abstract class MDBase : BlockHash, ICryptoNotBuildIn
    {
        protected readonly uint[] m_state;

        protected const uint C1 = 0x50a28be6;
        protected const uint C2 = 0x5a827999;
        protected const uint C3 = 0x5c4dd124;
        protected const uint C4 = 0x6ed9eba1;
        protected const uint C5 = 0x6d703ef3;
        protected const uint C6 = 0x8f1bbcdc;
        protected const uint C7 = 0x7a6d76e9;
        protected const uint C8 = 0xa953fd4e;

        protected MDBase(int a_state_length, int a_hash_size) 
            : base(a_hash_size, 64)
        {
            m_state = new uint[a_state_length];

            Initialize();
        }

        public override void Initialize()
        {
            m_state[0] = 0x67452301;
            m_state[1] = 0xefcdab89;
            m_state[2] = 0x98badcfe;
            m_state[3] = 0x10325476;

            base.Initialize();
        }

        protected override byte[] GetResult()
        {
            return Converters.ConvertUIntsToBytes(m_state);
        }

        protected override void Finish()
        {
            ulong bits = m_processed_bytes * 8;
            int padindex = (m_buffer.Pos < 56) ? (56 - m_buffer.Pos) : (120 - m_buffer.Pos);

            byte[] pad = new byte[padindex + 8];

            pad[0] = 0x80;

            Converters.ConvertULongToBytes(bits, pad, padindex);
            padindex += 8;

            TransformBytes(pad, 0, padindex);
        }
    }
}
