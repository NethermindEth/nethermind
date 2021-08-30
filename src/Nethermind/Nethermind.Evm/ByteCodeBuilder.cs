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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public class Prepare
    {
        private readonly List<byte> _byteCode = new();
        public static Prepare EvmCode => new();
        public byte[] Done => _byteCode.ToArray();

        public Prepare Op(byte instruction)
        {
            _byteCode.Add(instruction);
            return this;
        }
        
        public Prepare Op(Instruction instruction, int multiplier = 1)
        {
            for (int i = 0; i < multiplier; i++)
            {
                _byteCode.Add((byte)instruction);
            }
            return this;
        }

        public Prepare AddJumpDest(out int location)
        {
            _byteCode.Add((byte)Instruction.JUMPDEST);
            location = _byteCode.Count - 1;
            
            return this;
        }
        
        public Prepare Create(byte[] code, UInt256 value)
        {
            StoreDataInMemory(0, code);
            PushData(code.Length);
            PushData(0);
            PushData(value);
            Op(Instruction.CREATE);
            return this;
        }

        public Prepare Create(UInt256 codeLength, UInt256 memoryOffset, UInt256 value)
        {
            PushData(codeLength);
            PushData(memoryOffset);
            PushData(value);
            Op(Instruction.CREATE);
            return this;
        }
        
        public Prepare Create2(byte[] code, byte[] salt, UInt256 value)
        {
            StoreDataInMemory(0, code);
            PushData(salt);
            PushData(code.Length);
            PushData(0);
            PushData(value);
            Op(Instruction.CREATE2);
            return this;
        }

        public Prepare ExtCodeCopy(UInt256 codeLength, UInt256 codeOffset, UInt256 memoryOffset, Address address)
        {
            PushData(codeLength);
            PushData(codeOffset);
            PushData(memoryOffset);
            PushData(address);
            Op(Instruction.EXTCODECOPY);
            
            return this;
        }
        
        public Prepare ForInitOf(byte[] codeToBeDeployed)
        {
            StoreDataInMemory(0, codeToBeDeployed);
            PushData(codeToBeDeployed.Length);
            PushData(0);
            Op(Instruction.RETURN);

            return this;
        }
        
        public Prepare ForCreate2Of(byte[] codeToBeDeployed)
        {
            StoreDataInMemory(0, codeToBeDeployed);
            
            PushData(0); // salt
            PushData(codeToBeDeployed.Length);
            PushData(0); // position in memory
            Op(Instruction.CALLVALUE);
            Op(Instruction.CREATE2);

            return this;
        }

        public Prepare CallWithValue(Address address, long gasLimit)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            Op(Instruction.CALLVALUE); // value
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALL);
            return this;
        }
        
        public Prepare Call(Address address, long gasLimit)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0); // value
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALL);
            return this;
        }

        public Prepare CallWithInput(Address address, long gasLimit, string input)
        {
            return CallWithInput(address, gasLimit, Bytes.FromHexString(input));
        }

        public Prepare CallWithInput(Address address, long gasLimit, byte[] input)
        {
            StoreDataInMemory(0, input);
            PushData(0);
            PushData(0);
            PushData(input.Length);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALL);
            return this;
        }

        public Prepare CallWithValue(Address address, long gasLimit, UInt256 value)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(value);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALL);
            return this;
        }

        public Prepare DelegateCall(Address address, long gasLimit)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.DELEGATECALL);
            return this;
        }

        public Prepare CallCode(Address address, long gasLimit)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALLCODE);
            return this;
        }

        public Prepare StaticCall(Address address, long gasLimit)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.STATICCALL);
            return this;
        }

        public Prepare PushData(Address address)
        {
            PushData(address.Bytes);
            return this;
        }
        
        public Prepare PushData(int data)
        {
            return PushData((UInt256) data);
        }
        
        public Prepare PushData(long data)
        {
            return PushData((UInt256) data);
        }
        
        public Prepare PushData(UInt256 data)
        {
            Span<byte> bytes = stackalloc byte[32];
            data.ToBigEndian(bytes);
            
            PushData(bytes.WithoutLeadingZeros().ToArray());
            return this;
        }

        public Prepare PushData(string data)
        {
            PushData(Bytes.FromHexString(data));
            return this;
        }

        public Prepare PushData(byte[] data)
        {
            _byteCode.Add((byte) (Instruction.PUSH1 + (byte) data.Length - 1));
            _byteCode.AddRange(data);
            return this;
        }

        public Prepare PushData(byte data)
        {
            PushData(new[] {data});
            return this;
        }
        
        public Prepare FromCode(string data)
        {
            _byteCode.AddRange(Bytes.FromHexString(data));
            return this;
        }
        
        public Prepare Data(string data)
        {
            _byteCode.AddRange(Bytes.FromHexString(data));
            return this;
        }

        public Prepare Data(byte[] data)
        {
            _byteCode.AddRange(data);
            return this;
        }

        public Prepare Data(byte data)
        {
            _byteCode.Add(data);
            return this;
        }

        public Prepare PersistData(string key, string value)
        {
            PushData(value);
            PushData(key);
            Op(Instruction.SSTORE);
            return this;
        }

        public Prepare StoreDataInMemory(int position, string hexString)
        {
            return StoreDataInMemory(position, Bytes.FromHexString(hexString));
        }

        private Prepare StoreDataInMemory(int position, byte[] data)
        {
            for (int i = 0; i < data.Length; i += 32)
            {
                PushData(data.Slice(i, data.Length - i).PadRight(32));
                PushData(position + i);
                Op(Instruction.MSTORE);
            }

            return this;
        }
        
        public Prepare StoreDataInMemoryWithoutTrailingZeros(int position, byte[] data)
        {
            int i = 0;
                
            for (; i + 32 < data.Length; i += 32)
            {
                var dataSlice = data.Slice(i, data.Length - i).PadRightWithoutTrailingZeros(32);
                PushData(dataSlice);
                PushData(position + i);
                Op(Instruction.MSTORE);
            }

            for (; i < data.Length; i++)
            {
                var dataSlice = data[i];
                PushData(dataSlice);
                PushData(position + i);
                Op(Instruction.MSTORE8);
            }
            
            return this;
        }
    }
}
