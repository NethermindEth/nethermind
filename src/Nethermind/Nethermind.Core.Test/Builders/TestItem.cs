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
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders
{
    public static partial class TestItem
    {
        public static Random Random { get; } = new();
        private static AccountDecoder _accountDecoder = new();

        static TestItem()
        {
            NonZeroBloom = new Bloom();
            NonZeroBloom.Set(KeccakA.Bytes);

            PrivateKeys = new PrivateKey[255];
            PublicKeys = new PublicKey[255];
            Addresses = new Address[255];
            Keccaks = new Keccak[255];

            for (byte i = 1; i > 0; i++) // this will wrap around
            {
                byte[] bytes = new byte[32];
                bytes[31] = i;
                PrivateKeys[i - 1] = new PrivateKey(bytes);
                PublicKeys[i - 1] = PrivateKeys[i - 1].PublicKey;
                Addresses[i - 1] = PublicKeys[i - 1].Address;
                Keccaks[i - 1] = Keccak.Compute(PublicKeys[i - 1].Bytes);
            }
        }

        public static Keccak KeccakFromNumber(int i)
        {
            UInt256 keccakNumber = (UInt256)i;
            byte[] keccakBytes = new byte[32];
            keccakNumber.ToBigEndian(keccakBytes);
            return new Keccak(keccakBytes);
        }

        public static byte[] RandomDataA = { 1, 2, 3 };
        public static byte[] RandomDataB = { 4, 5, 6, 7 };
        public static byte[] RandomDataC = { 1, 2, 8, 9, 10 };

        public static Keccak KeccakA = Keccak.Compute("A");
        public static Keccak KeccakB = Keccak.Compute("B");
        public static Keccak KeccakC = Keccak.Compute("C");
        public static Keccak KeccakD = Keccak.Compute("D");
        public static Keccak KeccakE = Keccak.Compute("E");
        public static Keccak KeccakF = Keccak.Compute("F");
        public static Keccak KeccakG = Keccak.Compute("G");
        public static Keccak KeccakH = Keccak.Compute("H");

        public static PrivateKey PrivateKeyA = new("010102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyB = new("020102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyC = new("030102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyD = new("040102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyE = new("050102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        public static PrivateKey PrivateKeyF = new("060102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

        public static PublicKey PublicKeyA = PrivateKeyA.PublicKey;
        public static PublicKey PublicKeyB = PrivateKeyB.PublicKey;
        public static PublicKey PublicKeyC = PrivateKeyC.PublicKey;
        public static PublicKey PublicKeyD = PrivateKeyD.PublicKey;
        public static PublicKey PublicKeyE = PrivateKeyE.PublicKey;
        public static PublicKey PublicKeyF = PrivateKeyF.PublicKey;

        public static PrivateKey IgnoredPrivateKey = new("040102030405060708090a0b0c0d0e0f0001abe120919026fffff12155555555");
        public static PublicKey IgnoredPublicKey = IgnoredPrivateKey.PublicKey;

        public static PrivateKey[] PrivateKeys;
        public static PublicKey[] PublicKeys;
        public static Address[] Addresses;
        public static Keccak[] Keccaks;

        public static Address AddressA = PublicKeyA.Address;
        public static Address AddressB = PublicKeyB.Address;
        public static Address AddressC = PublicKeyC.Address;
        public static Address AddressD = PublicKeyD.Address;
        public static Address AddressE = PublicKeyE.Address;
        public static Address AddressF = PublicKeyF.Address;

        public static IPEndPoint IPEndPointA = IPEndPoint.Parse("10.0.0.1");
        public static IPEndPoint IPEndPointB = IPEndPoint.Parse("10.0.0.2");
        public static IPEndPoint IPEndPointC = IPEndPoint.Parse("10.0.0.3");
        public static IPEndPoint IPEndPointD = IPEndPoint.Parse("10.0.0.4");
        public static IPEndPoint IPEndPointE = IPEndPoint.Parse("10.0.0.5");
        public static IPEndPoint IPEndPointF = IPEndPoint.Parse("10.0.0.6");

        public static Bloom NonZeroBloom;

        public static Address GetRandomAddress(Random? random = null)
        {
            byte[] bytes = new byte[20];
            (random ?? Random).NextBytes(bytes);
            return new Address(bytes);
        }

        public static Keccak GetRandomKeccak(Random? random = null)
        {
            byte[] bytes = new byte[32];
            (random ?? Random).NextBytes(bytes);
            return new Keccak(bytes);
        }

        public static Account GenerateRandomAccount(Random random = null)
        {
            random ??= Random;

            Account account = new(
                (UInt256)random.Next(1000),
                (UInt256)random.Next(1000),
                Keccak.EmptyTreeHash,
                Keccak.OfAnEmptyString);

            return account;
        }

        public static byte[] GenerateRandomAccountRlp(AccountDecoder accountDecoder = null)
        {
            accountDecoder ??= _accountDecoder;
            Account account = GenerateRandomAccount();
            byte[] value = accountDecoder.Encode(account).Bytes;
            return value;
        }

        public static Account GenerateIndexedAccount(int index)
        {
            Account account = new(
                (UInt256)index,
                (UInt256)index,
                Keccak.EmptyTreeHash,
                Keccak.OfAnEmptyString);

            return account;
        }

        public static byte[] GenerateIndexedAccountRlp(int index, AccountDecoder accountDecoder = null)
        {
            accountDecoder ??= _accountDecoder;

            Account account = GenerateIndexedAccount(index);
            byte[] value = accountDecoder.Encode(account).Bytes;
            return value;
        }
    }
}
