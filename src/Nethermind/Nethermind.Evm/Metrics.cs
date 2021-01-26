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

using System.ComponentModel;

namespace Nethermind.Evm
{
    public class Metrics
    {
        [Description("Number of EVM exceptions thrown by contracts.")]
        public static long EvmExceptions { get; set; }
        
        [Description("Number of SELFDESTRUCT calls.")]
        public static long SelfDestructs { get; set; }
        
        [Description("Number of calls to other contracts.")]
        public static long Calls { get; set; }
        
        [Description("Number of SLOAD opcodes executed.")]
        public static long SloadOpcode { get; set; }
        
        [Description("Number of SSTORE opcodes executed.")]
        public static long SstoreOpcode { get; set; }
        
        [Description("Number of MODEXP precompiles executed.")]
        public static long ModExpOpcode { get; set; }
        
        [Description("Number of BLOCKHASH opcodes executed.")]
        public static long BlockhashOpcode { get; set; }
        
        [Description("Number of BN256_MUL precompile calls.")]
        public static long Bn256MulPrecompile { get; set; }
        
        [Description("Number of BN256_ADD precompile calls.")]
        public static long Bn256AddPrecompile { get; set; }
        
        [Description("Number of BN256_PAIRING precompile calls.")]
        public static long Bn256PairingPrecompile { get; set; }
        
        [Description("Number of EC_RECOVERY precompile calls.")]
        public static long EcRecoverPrecompile { get; set; }
        
        [Description("Number of MODEXP precompile calls.")]
        public static long ModExpPrecompile { get; set; }
        
        [Description("Number of RIPEMD160 precompile calls.")]
        public static long Ripemd160Precompile { get; set; }
        
        [Description("Number of SHA256 precompile calls.")]
        public static long Sha256Precompile { get; set; }
    }
}
