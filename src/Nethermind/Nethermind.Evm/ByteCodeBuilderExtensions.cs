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
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Org.BouncyCastle.Asn1.Mozilla;

namespace Nethermind.Evm
{
    /// <summary>
    /// A utility class that extends Prepare class to abstract stack-like behaviour
    /// </summary>
    ///

    //Note(Ayman) : redesign arg order to add flexibility and ability to use args already in stack, and add tests
    public static class BytecodeBuilder
    {
        public static Prepare Stop(this Prepare @this)
        {
            return @this.Op(Instruction.STOP);
        }
        public static Prepare Add(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.ADD);
        }
        public static Prepare Mul(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.MUL);
        }
        public static Prepare SUB(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.SUB);
        }
        public static Prepare DIV(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.DIV);
        }
        public static Prepare SDIV(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.SDIV);
        }
        public static Prepare MOD(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.MOD);
        }
        public static Prepare SMOD(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            return @this.Op(Instruction.SMOD);
        }
        public static Prepare ADDMOD(this Prepare @this, UInt256? a = null, UInt256? b = null, UInt256? m = null)
        {
            if (m is not null)
            {
                @this.PushData(m.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.ADDMOD);
        }
        public static Prepare MULMOD(this Prepare @this, UInt256? a = null, UInt256? b = null, UInt256? m = null)
        {
            if (m is not null)
            {
                @this.PushData(m.Value);
            }

            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.MULMOD);
        }
        public static Prepare SIGNEXTEND(this Prepare @this, UInt256? a = null, byte[] bytes = null)
        {
            if (bytes is not null)
            {
                @this.PushData(bytes);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SIGNEXTEND);
        }
        public static Prepare EXP(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.EXP);
        }
        public static Prepare LT(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.LT);
        }
        public static Prepare GT(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.GT);
        }
        public static Prepare SLT(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SLT);
        }
        public static Prepare SGT(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SGT);
        }
        public static Prepare EQ(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.EQ);
        }
        public static Prepare ISZERO(this Prepare @this, UInt256? a = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.ISZERO);
        }
        public static Prepare AND(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.AND);
        }
        public static Prepare OR(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.OR);
        }
        public static Prepare XOR(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.XOR);
        }
        public static Prepare NOT(this Prepare @this, UInt256? a = null)
        {
            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.NOT);
        }
        public static Prepare BYTE(this Prepare @this, UInt256? pos, byte[] bytes = null)
        {
            if (bytes is not null)
            {
                @this.PushData(bytes);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.BYTE);
        }
        public static Prepare SHA3(this Prepare @this, UInt256? pos = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.SHA3);
        }
        public static Prepare ADDRESS(this Prepare @this)
        {
            return @this.Op(Instruction.ADDRESS);
        }
        public static Prepare BALANCE(this Prepare @this, Address? address = null)
        {
            if(address is not null)
            {
                @this.PushData(address);
            }

            return @this.Op(Instruction.BALANCE);
        }
        public static Prepare CALLER(this Prepare @this)
        {
            return @this.Op(Instruction.CALLER);
        }
        public static Prepare CALLVALUE(this Prepare @this)
        {
            return @this.Op(Instruction.CALLVALUE);
        }
        public static Prepare ORIGIN(this Prepare @this)
        {
            return @this.Op(Instruction.ORIGIN);
        }
        public static Prepare CALLDATALOAD(this Prepare @this, UInt256? src = null)
        {
            if(src is not null)
            {
                @this.PushData(src.Value);
            }
            return @this.Op(Instruction.ORIGIN);
        }
        public static Prepare CALLDATASIZE(this Prepare @this)
        {
            return @this.Op(Instruction.CALLDATASIZE);
        }
        public static Prepare CALLDATACOPY(this Prepare @this, UInt256? src = null, UInt256? dest = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (dest is not null)
            {
                @this.PushData(dest.Value);
            }

            if (src is not null)
            {
                @this.PushData(src.Value);
            }

            return @this.Op(Instruction.CALLDATACOPY);
        }
        public static Prepare CODESIZE(this Prepare @this)
        {
            return @this.Op(Instruction.CODESIZE);
        }
        public static Prepare CODECOPY(this Prepare @this, UInt256? src = null, UInt256? dest = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (dest is not null)
            {
                @this.PushData(dest.Value);
            }

            if (src is not null)
            {
                @this.PushData(src.Value);
            }

            return @this.Op(Instruction.CODECOPY);
        }
        public static Prepare GASPRICE(this Prepare @this)
        {
            return @this.Op(Instruction.GASPRICE);
        }
        public static Prepare EXTCODESIZE(this Prepare @this, Address? address = null)
        {
            if (address is not null)
            {
                @this.PushData(address);
            }

            return @this.Op(Instruction.EXTCODESIZE);
        }
        public static Prepare CODECOPY(this Prepare @this, Address? address, UInt256? src = null, UInt256? dest = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (dest is not null)
            {
                @this.PushData(dest.Value);
            }

            if (src is not null)
            {
                @this.PushData(src.Value);
            }

            if (address is not null)
            {
                @this.PushData(address);
            }

            return @this.Op(Instruction.EXTCODECOPY);
        }
        public static Prepare RETURNDATASIZE(this Prepare @this)
        {
            return @this.Op(Instruction.RETURNDATASIZE);
        }
        public static Prepare RETURNDATACOPY(this Prepare @this, UInt256? src = null, UInt256? dest = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (dest is not null)
            {
                @this.PushData(dest.Value);
            }

            if (src is not null)
            {
                @this.PushData(src.Value);
            }

            return @this.Op(Instruction.RETURNDATACOPY);
        }
        public static Prepare BLOCKHASH(this Prepare @this, UInt256? target = null)
        {
            if(target is not null)
            {
                @this.PushData(target.Value);
            }
            return @this.Op(Instruction.BLOCKHASH);
        }
        public static Prepare COINBASE(this Prepare @this)
        {
            return @this.Op(Instruction.COINBASE);
        }
        public static Prepare PREVRANDAO(this Prepare @this)
        {
            return @this.Op(Instruction.PREVRANDAO);
        }
        public static Prepare TIMESTAMP(this Prepare @this)
        {
            return @this.Op(Instruction.TIMESTAMP);
        }
        public static Prepare NUMBER(this Prepare @this)
        {
            return @this.Op(Instruction.NUMBER);
        }
        public static Prepare GASLIMIT(this Prepare @this)
        {
            return @this.Op(Instruction.GASLIMIT);
        }
        public static Prepare CHAINID(this Prepare @this)
        {
            return @this.Op(Instruction.CHAINID);
        }
        public static Prepare SELFBALANCE(this Prepare @this)
        {
            return @this.Op(Instruction.SELFBALANCE);
        }
        public static Prepare BASEFEE(this Prepare @this)
        {
            return @this.Op(Instruction.BASEFEE);
        }
        public static Prepare POP(this Prepare @this)
        {
            return @this.Op(Instruction.POP);
        }
        public static Prepare MSTORE(this Prepare @this, byte[] data = null, UInt256? pos = null)
        {
            if (data is not null)
            {
                @this.PushData(data);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.MSTORE);
        }
        public static Prepare MSTORE8(this Prepare @this, byte[] data = null, UInt256 ? pos = null)
        {
            if (data is not null)
            {
                @this.PushData(data);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.MSTORE8);
        }
        public static Prepare TSTORE(this Prepare @this, byte[] data = null, UInt256? pos = null)
        {
            if (data is not null)
            {
                @this.PushData(data);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.TSTORE);
        }
        public static Prepare SSTORE(this Prepare @this, byte[] data = null, UInt256? pos = null)
        {
            if (data is not null)
            {
                @this.PushData(data);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.SSTORE);
        }
        public static Prepare MLOAD(this Prepare @this, UInt256? pos = null)
        {
            if(pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.MLOAD);
        }
        public static Prepare TLOAD(this Prepare @this, UInt256? pos = null)
        {
            if(pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.TLOAD);
        }
        public static Prepare SLOAD(this Prepare @this, UInt256? pos = null)
        {
            if(pos is not null)
            {
                @this.PushData(pos.Value);
            }
            return @this.Op(Instruction.SLOAD);
        }
        public static Prepare JUMP(this Prepare @this, UInt256? to = null)
        {
            if (to is not null)
            {
                @this.PushData(to.Value);
            }
            return @this.Op(Instruction.JUMP);
        }
        public static Prepare JUMPI(this Prepare @this, byte[] cond = null, UInt256? to = null)
        {
            if(cond is not null)
            {
                @this.PushData(cond);
            }

            if (to is not null)
            {
                @this.PushData(to.Value);
            }
            return @this.Op(Instruction.JUMP);
        }
        public static Prepare PC(this Prepare @this)
        {
            return @this.Op(Instruction.PC);
        }
        public static Prepare GAS(this Prepare @this)
        {
            return @this.Op(Instruction.GAS);
        }
        public static Prepare JUMPDEST(this Prepare @this)
        {
            return @this.Op(Instruction.JUMPDEST);
        }
        public static Prepare MSIZE(this Prepare @this)
        {
            return @this.Op(Instruction.MSIZE);
        }
        public static Prepare SWAPx(this Prepare @this, byte i)
        {
            return @this.Op(Instruction.SWAP1 + i - 1);
        }
        public static Prepare DUPx(this Prepare @this, byte i)
        {
            return @this.Op(Instruction.DUP1 + i - 1);
        }
        public static Prepare PUSHx(this Prepare @this, byte[] args)
        {
            return @this.PushData(args);
        }
        public static Prepare LOGx(this Prepare @this, byte i, UInt256? len = null, UInt256? pos = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.LOG0 + i);
        }
        public static Prepare CREATE(this Prepare @this, byte i, UInt256? value = null, UInt256? initCodePos = null, UInt256? initCodeLen = null)
        {
            if (initCodeLen is not null)
            {
                @this.PushData(initCodeLen.Value);
            }

            if (initCodePos is not null)
            {
                @this.PushData(initCodePos.Value);
            }

            if (value is not null)
            {
                @this.PushData(value.Value);
            }
            return @this.Op(i == 2 ? Instruction.CREATE2 : Instruction.CREATE);
        }
        public static Prepare RETURN(this Prepare @this, UInt256? pos = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.RETURN);
        }
        public static Prepare CALL(this Prepare @this, UInt256? gasLim = null,
                                        Address? address = null, UInt256? callValue = null,
                                        UInt256? dataOffset = null, UInt256? dataLength = null,
                                        UInt256? outputOffset = null, UInt256? outputLength = null)
        {
            if (outputLength is not null)
            {
                @this.PushData(outputLength.Value);
            }

            if (outputOffset is not null)
            {
                @this.PushData(outputOffset.Value);
            }

            if (dataLength is not null)
            {
                @this.PushData(dataLength.Value);
            }

            if (dataOffset is not null)
            {
                @this.PushData(dataOffset.Value);
            }

            if (callValue is not null)
            {
                @this.PushData(callValue.Value);
            }

            if (address is not null)
            {
                @this.PushData(address);
            }

            if (gasLim is not null)
            {
                @this.PushData(gasLim.Value);
            }

            return @this.Op(Instruction.CALL);
        }
        public static Prepare CALLCODE(this Prepare @this, UInt256? gasLim = null,
                                            Address? address = null, UInt256? callValue = null,
                                            UInt256? dataOffset = null, UInt256? dataLength = null,
                                            UInt256? outputOffset = null, UInt256? outputLength = null)
        {
            if (outputLength is not null)
            {
                @this.PushData(outputLength.Value);
            }

            if (outputOffset is not null)
            {
                @this.PushData(outputOffset.Value);
            }

            if (dataLength is not null)
            {
                @this.PushData(dataLength.Value);
            }

            if (dataOffset is not null)
            {
                @this.PushData(dataOffset.Value);
            }

            if (callValue is not null)
            {
                @this.PushData(callValue.Value);
            }

            if (address is not null)
            {
                @this.PushData(address);
            }

            if (gasLim is not null)
            {
                @this.PushData(gasLim.Value);
            }

            return @this.Op(Instruction.CALLCODE);
        }
        public static Prepare DELEGATECODE(this Prepare @this, UInt256? gasLim = null,
                                                Address? address = null, UInt256? dataOffset = null,
                                                UInt256? dataLength = null, UInt256? outputOffset = null,
                                                UInt256? outputLength = null)
        {
            if (outputLength is not null)
            {
                @this.PushData(outputLength.Value);
            }

            if (outputOffset is not null)
            {
                @this.PushData(outputOffset.Value);
            }

            if (dataLength is not null)
            {
                @this.PushData(dataLength.Value);
            }

            if (dataOffset is not null)
            {
                @this.PushData(dataOffset.Value);
            }

            if (address is not null)
            {
                @this.PushData(address);
            }

            if (gasLim is not null)
            {
                @this.PushData(gasLim.Value);
            }

            return @this.Op(Instruction.DELEGATECALL);
        }
        public static Prepare STATICCALL(this   Prepare @this, UInt256? gasLim = null,
                                                Address? address = null, UInt256? dataOffset = null,
                                                UInt256? dataLength = null, UInt256? outputOffset = null,
                                                UInt256? outputLength = null)
        {
            if (outputLength is not null)
            {
                @this.PushData(outputLength.Value);
            }

            if (outputOffset is not null)
            {
                @this.PushData(outputOffset.Value);
            }

            if (dataLength is not null)
            {
                @this.PushData(dataLength.Value);
            }

            if (dataOffset is not null)
            {
                @this.PushData(dataOffset.Value);
            }

            if (address is not null)
            {
                @this.PushData(address);
            }

            if (gasLim is not null)
            {
                @this.PushData(gasLim.Value);
            }

            return @this.Op(Instruction.STATICCALL);
        }

        public static Prepare REVERT(this Prepare @this, UInt256? pos = null, UInt256? len = null)
        {
            if (len is not null)
            {
                @this.PushData(len.Value);
            }

            if (pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.REVERT);
        }
        public static Prepare INVALID(this Prepare @this)
        {
            return @this.Op(Instruction.INVALID);
        }
        public static Prepare SELFDESTRUCT(this Prepare @this, Address? address = null)
        {
            if (address is not null)
            {
                @this.PushData(address);
            }

            return @this.Op(Instruction.SELFDESTRUCT);
        }
        public static Prepare EXTCODEHASH(this Prepare @this, Address? address = null)
        {
            if (address is not null)
            {
                @this.PushData(address);
            }

            return @this.Op(Instruction.EXTCODEHASH);
        }
        public static Prepare BEGINSUB(this Prepare @this)
        {
            return @this.Op(Instruction.BEGINSUB);
        }
        public static Prepare RETURNSUB(this Prepare @this)
        {
            return @this.Op(Instruction.RETURNSUB); 
        }
        public static Prepare JUMPSUB(this Prepare @this, UInt256? pos = null)
        {
            if(pos is not null)
            {
                @this.PushData(pos.Value);
            }

            return @this.Op(Instruction.JUMPSUB);
        }
        public static Prepare SHL(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SHL);
        }
        public static Prepare SHR(this Prepare @this, UInt256? a = null, UInt256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(b.Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SHR);
        }
        public static Prepare SAR(this Prepare @this, UInt256? a = null, Int256.Int256? b = null)
        {
            if (b is not null)
            {
                @this.PushData(((UInt256?)b).Value);
            }

            if (a is not null)
            {
                @this.PushData(a.Value);
            }

            return @this.Op(Instruction.SAR);
        }
    }
}
