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

namespace Nethermind.Evm
{
    public class Metrics
    {
        public static long EvmExceptions { get; set; }
        public static long SelfDestructs { get; set; }
        public static long Calls { get; set; }
        public static long SloadOpcode { get; set; }
        public static long SstoreOpcode { get; set; }
        public static long ModExpOpcode { get; set; }
        public static long BlockhashOpcode { get; set; }
        public static long Bn128MulPrecompile { get; set; }
        public static long Bn128AddPrecompile { get; set; }
        public static long Bn128PairingPrecompile { get; set; }
        public static long EcRecoverPrecompile { get; set; }
        public static long ModExpPrecompile { get; set; }
        public static long Ripemd160Precompile { get; set; }
        public static long Sha256Precompile { get; set; }
        public static long Blake2BPrecompile { get; set; }
    }
}