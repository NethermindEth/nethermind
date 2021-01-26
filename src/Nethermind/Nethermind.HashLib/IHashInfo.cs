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

namespace Nethermind.HashLib
{
    public interface ICrypto : IHash, IBlockHash
    {
    }

    public interface ICryptoBuildIn : ICrypto
    {
    }

    public interface ICryptoNotBuildIn : ICrypto
    {
    }

    public interface IHMAC : IWithKey, ICrypto
    {
    }

    public interface IHMACBuildIn : IHMAC, ICryptoBuildIn
    {
    }

    public interface IHMACNotBuildIn : IHMAC, ICryptoNotBuildIn
    {
    }

    public interface IHasHMACBuildIn : ICrypto
    {
        System.Security.Cryptography.HMAC GetBuildHMAC();
    }

    public interface IHash32 : IHash
    {
    }

    public interface IHash128 : IHash
    {
    }

    public interface IHash64 : IHash
    {
    }

    public interface IWithKey : IHash
    {
        byte[] Key
        {
            get;
            set;
        }

        int? KeyLength
        {
            get;
        }
    }

    public interface IHashWithKey : IHash, IWithKey
    {
    }

    public interface IFastHash32
    {
        int ComputeByteFast(byte a_data);
        int ComputeCharFast(char a_data);
        int ComputeShortFast(short a_data);
        int ComputeUShortFast(ushort a_data);
        int ComputeIntFast(int a_data);
        int ComputeUIntFast(uint a_data);
        int ComputeLongFast(long a_data);
        int ComputeULongFast(ulong a_data);
        int ComputeFloatFast(float a_data);
        int ComputeDoubleFast(double a_data);
        int ComputeStringFast(string a_data);
        int ComputeBytesFast(byte[] a_data);
        int ComputeCharsFast(char[] a_data);
        int ComputeShortsFast(short[] a_data);
        int ComputeUShortsFast(ushort[] a_data);
        int ComputeIntsFast(int[] a_data);
        int ComputeUIntsFast(uint[] a_data);
        int ComputeLongsFast(long[] a_data);
        int ComputeULongsFast(ulong[] a_data);
        int ComputeDoublesFast(double[] a_data);
        int ComputeFloatsFast(float[] a_data);
    }

    public interface IBlockHash
    {
    }

    public interface INonBlockHash
    {
    }

    public interface IChecksum
    {
    }
}
