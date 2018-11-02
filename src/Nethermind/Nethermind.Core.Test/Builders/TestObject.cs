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
    public static class TestObject
    {
        static TestObject()
        {
            NonZeroBloom = new Bloom();
            NonZeroBloom.Set(KeccakA.Bytes);
        }
        
        public static byte[] RandomDataA = {1, 2, 3};
        public static byte[] RandomDataB = {4, 5, 6, 7};
        public static byte[] RandomDataC = {1, 2, 8, 9, 10};

        public static Keccak KeccakA = Keccak.Compute("A");
        public static Keccak KeccakB = Keccak.Compute("B");
        public static Keccak KeccakC = Keccak.Compute("C");
        public static Keccak KeccakD = Keccak.Compute("D");
        public static Keccak KeccakE = Keccak.Compute("E");
        public static Keccak KeccakF = Keccak.Compute("F");
        public static Keccak KeccakG = Keccak.Compute("G");
        public static Keccak KeccakH = Keccak.Compute("H");

        public static PrivateKey PrivateKeyA = new PrivateKey("010102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyB = new PrivateKey("020102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyC = new PrivateKey("030102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyD = new PrivateKey("040102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

        public static PublicKey PublicKeyA = PrivateKeyA.PublicKey;
        public static PublicKey PublicKeyB = PrivateKeyB.PublicKey;
        public static PublicKey PublicKeyC = PrivateKeyC.PublicKey;
        public static PublicKey PublicKeyD = PrivateKeyD.PublicKey;

        public static Address AddressA = PublicKeyA.Address;
        public static Address AddressB = PublicKeyB.Address;
        public static Address AddressC = PublicKeyC.Address;
        public static Address AddressD = PublicKeyD.Address;

        public static Bloom NonZeroBloom;
    }
}