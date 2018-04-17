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
        public static Keccak KeccakA = Keccak.Compute("A");
        public static Keccak KeccakB = Keccak.Compute("B");
        public static Keccak KeccakC = Keccak.Compute("C");
        public static Keccak KeccakD = Keccak.Compute("D");
        public static Keccak KeccakE = Keccak.Compute("E");
        public static Keccak KeccakF = Keccak.Compute("F");
        public static Keccak KeccakG = Keccak.Compute("G");
        public static Keccak KeccakH = Keccak.Compute("H");

        public static PrivateKey PrivateKeyA = Build.A.PrivateKey.TestObject;
        public static PrivateKey PrivateKeyB = Build.A.PrivateKey.TestObject;
        public static PrivateKey PrivateKeyC = Build.A.PrivateKey.TestObject;
        public static PrivateKey PrivateKeyD = Build.A.PrivateKey.TestObject;
        
        public static PublicKey PublicKeyA = PrivateKeyA.PublicKey;
        public static PublicKey PublicKeyB = PrivateKeyB.PublicKey;
        public static PublicKey PublicKeyC = PrivateKeyC.PublicKey;
        public static PublicKey PublicKeyD = PrivateKeyD.PublicKey;
    }
}