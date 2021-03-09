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
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.HashLib
{
    internal abstract class MultipleTransformNonBlock : Hash, INonBlockHash
    {
        private List<ArraySegment<byte>> m_list = new();

        public MultipleTransformNonBlock(int a_hash_size, int a_block_size)
            : base(a_hash_size, a_block_size)
        {
        }

        public override void Initialize()
        {
            m_list.Clear();
        }

        public override void TransformBytes(byte[] a_data, int a_index, int a_length)
        {
            Debug.Assert(a_index >= 0);
            Debug.Assert(a_length >= 0);
            Debug.Assert(a_index + a_length <= a_data.Length);

            m_list.Add(new ArraySegment<byte>(a_data, a_index, a_length));
        }

        public override HashResult TransformFinal()
        {
            HashResult result = ComputeAggregatedBytes(Aggregate());
            Initialize();
            return result;
        }

        private byte[] Aggregate()
        {
            int sum = 0;
            foreach (ArraySegment<byte> seg in m_list)
                sum += seg.Count;

            byte[] res = new byte[sum];

            int index = 0;

            foreach (ArraySegment<byte> seg in m_list)
            {
                Array.Copy(seg.Array, seg.Offset, res, index, seg.Count);
                index += seg.Count;
            }

            return res;
        }

        public override HashResult ComputeBytes(byte[] a_data)
        {
            Initialize();
            return ComputeAggregatedBytes(a_data);
        }

        protected abstract HashResult ComputeAggregatedBytes(byte[] a_data);
    }
}
