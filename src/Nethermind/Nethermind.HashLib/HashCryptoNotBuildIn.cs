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

using System;
using System.Diagnostics;

namespace Nethermind.HashLib
{
    [DebuggerNonUserCode]
    public abstract class BlockHash : Hash, IBlockHash
    {
        protected readonly HashBuffer m_buffer;
        protected ulong m_processed_bytes;

        protected BlockHash(int a_hash_size, int a_block_size, int a_buffer_size = -1)
            : base(a_hash_size, a_block_size)
        {
            if (a_buffer_size == -1)
                a_buffer_size = a_block_size;

            m_buffer = new HashBuffer(a_buffer_size);
            m_processed_bytes = 0;
        }

        public override void TransformBytes(ReadOnlySpan<byte> a_data, int a_index, int a_length)
        {
            Debug.Assert(a_index >= 0);
            Debug.Assert(a_length >= 0);
            Debug.Assert(a_index + a_length <= a_data.Length);

            if (!m_buffer.IsEmpty)
            {
                if (m_buffer.Feed(a_data, ref a_index, ref a_length, ref m_processed_bytes))
                    TransformBuffer();
            }

            while (a_length >= m_buffer.Length)
            {
                m_processed_bytes += (ulong)m_buffer.Length;
                TransformBlock(a_data, a_index);
                a_index += m_buffer.Length;
                a_length -= m_buffer.Length;
            }

            if (a_length > 0)
                m_buffer.Feed(a_data, ref a_index, ref a_length, ref m_processed_bytes);
        }

        public override void TransformBytes(byte[] a_data, int a_index, int a_length)
        {
            Debug.Assert(a_index >= 0);
            Debug.Assert(a_length >= 0);
            Debug.Assert(a_index + a_length <= a_data.Length);

            if (!m_buffer.IsEmpty)
            {
                if (m_buffer.Feed(a_data, ref a_index, ref a_length, ref m_processed_bytes))
                    TransformBuffer();
            }

            while (a_length >= m_buffer.Length)
            {
                m_processed_bytes += (ulong)m_buffer.Length;
                TransformBlock(a_data, a_index);
                a_index += m_buffer.Length;
                a_length -= m_buffer.Length;
            }

            if (a_length > 0)
                m_buffer.Feed(a_data, ref a_index, ref a_length, ref m_processed_bytes);
        }

        public override void Initialize()
        {
            m_buffer.Initialize();
            m_processed_bytes = 0;
        }

        public override HashResult TransformFinal()
        {
            Finish();

            Debug.Assert(m_buffer.IsEmpty);

            byte[] result = GetResult();

            Debug.Assert(result.Length == HashSize);

            Initialize();
            return new HashResult(result);
        }

        public override uint[] TransformFinalUInts()
        {
            uint[] result = new uint[HashSize / 4];
            TransformFinalUInts(result);
            return result;
        }
        
        public override void TransformFinalUInts(Span<uint> output)
        {
            Finish();

            Debug.Assert(m_buffer.IsEmpty);

            GetResultUInts(output);

            Debug.Assert(output.Length == HashSize / 4);

            Initialize();
        }

        protected void TransformBuffer()
        {
            Debug.Assert(m_buffer.IsFull);

            TransformBlock(m_buffer.GetBytes(), 0);
        }

        protected abstract void Finish();
        protected abstract void TransformBlock(byte[] a_data, int a_index);
        //protected abstract void TransformBlock(Span<byte> a_data, int a_index);
        protected virtual void TransformBlock(ReadOnlySpan<byte> a_data, int a_index)
        {
            throw new NotImplementedException();
        }

        protected abstract byte[] GetResult();

        protected virtual uint[] GetResultUInts()
        {
            throw new NotSupportedException();
        }
        
        protected virtual void GetResultUInts(Span<uint> result)
        {
            throw new NotSupportedException();
        }
    }
}
