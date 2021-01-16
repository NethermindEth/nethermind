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
using System.IO;
using System.Text;

namespace Nethermind.HashLib
{
    public interface IHash
    {
        string Name { get; }
        int BlockSize { get; }
        int HashSize { get; }

        HashResult ComputeObject(object a_data);
        HashResult ComputeByte(byte a_data);
        HashResult ComputeChar(char a_data);
        HashResult ComputeShort(short a_data);
        HashResult ComputeUShort(ushort a_data);
        HashResult ComputeInt(int a_data);
        HashResult ComputeUInt(uint a_data);
        HashResult ComputeLong(long a_data);
        HashResult ComputeULong(ulong a_data);
        HashResult ComputeFloat(float a_data);
        HashResult ComputeDouble(double a_data);
        HashResult ComputeString(string a_data);
        HashResult ComputeString(string a_data, Encoding a_encoding);
        HashResult ComputeBytes(byte[] a_data);
        HashResult ComputeBytes(ReadOnlySpan<byte> a_data);
        HashResult ComputeChars(char[] a_data);
        HashResult ComputeShorts(short[] a_data);
        HashResult ComputeUShorts(ushort[] a_data);
        HashResult ComputeInts(int[] a_data);
        HashResult ComputeUInts(uint[] a_data);
        HashResult ComputeLongs(long[] a_data);
        HashResult ComputeULongs(ulong[] a_data);
        HashResult ComputeDoubles(double[] a_data);
        HashResult ComputeFloats(float[] a_data);
        HashResult ComputeStream(Stream a_stream, long a_length = -1);
        HashResult ComputeFile(string a_file_name, long a_from = 0, long a_length = -1);

        void Initialize();

        void TransformBytes(byte[] a_data);
        void TransformBytes(byte[] a_data, int a_index);
        void TransformBytes(byte[] a_data, int a_index, int a_length);

        HashResult TransformFinal();

        void TransformObject(object a_data);
        void TransformByte(byte a_data);
        void TransformChar(char a_data);
        void TransformShort(short a_data);
        void TransformUShort(ushort a_data);
        void TransformInt(int a_data);
        void TransformUInt(uint a_data);
        void TransformLong(long a_data);
        void TransformULong(ulong a_data);
        void TransformFloat(float a_data);
        void TransformDouble(double a_data);
        void TransformString(string a_data);
        void TransformString(string a_data, Encoding a_encoding);
        void TransformChars(char[] a_data);
        void TransformShorts(short[] a_data);
        void TransformUShorts(ushort[] a_data);
        void TransformInts(int[] a_data);
        void TransformUInts(Span<uint> a_data);
        void TransformLongs(long[] a_data);
        void TransformULongs(ulong[] a_data);
        void TransformDoubles(double[] a_data);
        void TransformFloats(float[] a_data);
        void TransformStream(Stream a_stream, long a_length = -1);
        void TransformFile(string a_file_name, long a_from = 0, long a_length = -1);
    }
}
