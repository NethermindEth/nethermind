// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    /// <summary>
    /// A utility class that extends Prepare class to abstract stack-like behaviour
    /// </summary>
    ///
    public static class BytecodeBuilder
    {
        #region helper_functions
        internal static Prepare PushSingle(this Prepare @this, UInt256? value)
        {
            if (value is not null)
            {
                return @this.PushData(value.Value);
            }
            return @this;
        }
        internal static Prepare PushSingle(this Prepare @this, byte[] value)
        {
            if (value is not null)
            {
                return @this.PushData(value);
            }
            return @this;
        }
        internal static Prepare PushSingle(this Prepare @this, Address? value)
        {
            if (value is not null)
            {
                return @this.PushData(value);
            }
            return @this;
        }
        internal static Prepare PushSequence(this Prepare @this, params UInt256?[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                @this.PushSingle(args[i]);
            }
            return @this;
        }


        internal static Prepare PutSequence(this Prepare @this, params Instruction[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                @this.Op(args[i]);
            }
            return @this;
        }

        internal static Prepare DataTable(this Prepare @this, short[] table)
        {
            for (int i = 0; i < table.Length; i++)
            {
                @this.Data(BitConverter.GetBytes(table[i]).Reverse().ToArray());
            }
            return @this;
        }
        #endregion

        public static Prepare COMMENT(this Prepare @this, string comment) => @this;

        #region opcodes_with_0_arg
        public static Prepare STOP(this Prepare @this)
            => @this.Op(Instruction.STOP);

        public static Prepare ADDRESS(this Prepare @this)
            => @this.Op(Instruction.ADDRESS);
        public static Prepare CALLER(this Prepare @this)
            => @this.Op(Instruction.CALLER);
        public static Prepare CALLVALUE(this Prepare @this)
            => @this.Op(Instruction.CALLVALUE);
        public static Prepare ORIGIN(this Prepare @this)
            => @this.Op(Instruction.ORIGIN);
        public static Prepare CALLDATASIZE(this Prepare @this)
            => @this.Op(Instruction.CALLDATASIZE);
        public static Prepare CODESIZE(this Prepare @this)
            => @this.Op(Instruction.CODESIZE);
        public static Prepare GASPRICE(this Prepare @this)
            => @this.Op(Instruction.GASPRICE);
        public static Prepare RETURNDATASIZE(this Prepare @this)
            => @this.Op(Instruction.RETURNDATASIZE);
        public static Prepare COINBASE(this Prepare @this)
            => @this.Op(Instruction.COINBASE);
        public static Prepare PREVRANDAO(this Prepare @this)
            => @this.Op(Instruction.PREVRANDAO);
        public static Prepare TIMESTAMP(this Prepare @this)
            => @this.Op(Instruction.TIMESTAMP);
        public static Prepare NUMBER(this Prepare @this)
            => @this.Op(Instruction.NUMBER);
        public static Prepare GASLIMIT(this Prepare @this)
            => @this.Op(Instruction.GASLIMIT);
        public static Prepare CHAINID(this Prepare @this)
            => @this.Op(Instruction.CHAINID);
        public static Prepare SELFBALANCE(this Prepare @this)
            => @this.Op(Instruction.SELFBALANCE);
        public static Prepare BASEFEE(this Prepare @this)
            => @this.Op(Instruction.BASEFEE);
        public static Prepare DATAHASH(this Prepare @this)
            => @this.Op(Instruction.DATAHASH);
        public static Prepare POP(this Prepare @this)
            => @this.Op(Instruction.POP);
        public static Prepare PC(this Prepare @this)
            => @this.Op(Instruction.PC);
        public static Prepare GAS(this Prepare @this)
            => @this.Op(Instruction.GAS);
        public static Prepare JUMPDEST(this Prepare @this)
            => @this.Op(Instruction.JUMPDEST);
        public static Prepare NOP(this Prepare @this)
            => @this.Op(Instruction.NOP);
        public static Prepare MSIZE(this Prepare @this)
            => @this.Op(Instruction.MSIZE);
        public static Prepare SWAPx(this Prepare @this, byte i)
            => @this.Op(Instruction.SWAP1 + i - 1);
        public static Prepare DUPx(this Prepare @this, byte i)
            => @this.Op(Instruction.DUP1 + i - 1);
        public static Prepare BEGINSUB(this Prepare @this)
            => @this.Op(Instruction.BEGINSUB);
        public static Prepare RETURNSUB(this Prepare @this)
            => @this.Op(Instruction.RETURNSUB);
        public static Prepare INVALID(this Prepare @this)
            => @this.Op(Instruction.INVALID);
        #endregion

        #region opcodes_with_1_arg
        public static Prepare SELFDESTRUCT(this Prepare @this, Address? address = null)
            => @this.PushSingle(address)
                    .Op(Instruction.SELFDESTRUCT);
        public static Prepare EXTCODEHASH(this Prepare @this, Address? address = null)
            => @this.PushSingle(address)
                    .Op(Instruction.EXTCODEHASH);
        public static Prepare JUMPSUB(this Prepare @this, UInt256? pos = null)
            => @this.PushSingle(pos)
                    .Op(Instruction.JUMPSUB);
        public static Prepare MLOAD(this Prepare @this, UInt256? pos = null)
            => @this.PushSingle(pos)
                    .Op(Instruction.MLOAD);
        public static Prepare TLOAD(this Prepare @this, UInt256? pos = null)
            => @this.PushSingle(pos)
                    .Op(Instruction.TLOAD);
        public static Prepare SLOAD(this Prepare @this, UInt256? pos = null)
            => @this.PushSingle(pos)
                    .Op(Instruction.SLOAD);
        public static Prepare JUMP(this Prepare @this, UInt256? to = null)
            => @this.PushSingle(to)
                    .Op(Instruction.JUMP);
        public static Prepare RJUMP(this Prepare @this, Int16 to)
            => @this.Op(Instruction.RJUMP)
                    .Data(BitConverter.GetBytes(to).Reverse().ToArray());
        public static Prepare RJUMPV(this Prepare @this, Int16[] table, UInt256? to = null)
            => @this.PushSingle(to)
                    .Op(Instruction.RJUMPV)
                    .Data((byte)table.Length)
                    .DataTable(table);

        public static Prepare RETF(this Prepare @this)
            => @this.Op(Instruction.RETF);

        public static Prepare BLOCKHASH(this Prepare @this, UInt256? target = null)
            => @this.PushSingle(target)
                    .Op(Instruction.BLOCKHASH);
        public static Prepare EXTCODESIZE(this Prepare @this, Address? address = null)
            => @this.PushSingle(address)
                    .Op(Instruction.EXTCODESIZE);
        public static Prepare CALLDATALOAD(this Prepare @this, UInt256? src = null)
            => @this.PushSingle(src)
                    .Op(Instruction.CALLDATALOAD);
        public static Prepare BALANCE(this Prepare @this, Address? address = null)
            => @this.PushSingle(address)
                    .Op(Instruction.BALANCE);
        public static Prepare NOT(this Prepare @this, UInt256? val = null)
            => @this.PushSingle(val)
                    .Op(Instruction.NOT);
        public static Prepare ISZERO(this Prepare @this, UInt256? val = null)
            => @this.PushSingle(val)
                    .Op(Instruction.ISZERO);
        #endregion

        #region opcodes_with_2_args
        public static Prepare ADD(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.ADD);
        public static Prepare MUL(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.MUL);
        public static Prepare SUB(this Prepare @this, UInt256? from = null, UInt256? value = null)
            => @this.PushSequence(value, from)
                    .Op(Instruction.SUB);
        public static Prepare DIV(this Prepare @this, UInt256? dividend = null, UInt256? divider = null)
            => @this.PushSequence(divider, dividend)
                    .Op(Instruction.DIV);
        public static Prepare SDIV(this Prepare @this, UInt256? dividend = null, UInt256? divider = null)
            => @this.PushSequence(divider, dividend)
                    .Op(Instruction.SDIV);
        public static Prepare MOD(this Prepare @this, UInt256? lhs = null, UInt256? mod = null)
            => @this.PushSequence(mod, lhs)
                    .Op(Instruction.MOD);
        public static Prepare SMOD(this Prepare @this, UInt256? lhs = null, UInt256? mod = null)
            => @this.PushSequence(mod, lhs)
                    .Op(Instruction.SMOD);
        public static Prepare SIGNEXTEND(this Prepare @this, UInt256? arg = null, byte[] bytes = null)
            => @this.PushSingle(bytes)
                    .PushSingle(arg)
                    .Op(Instruction.SIGNEXTEND);
        public static Prepare EXP(this Prepare @this, UInt256? exp = null, UInt256? @base = null)
            => @this.PushSequence(@base, exp)
                    .Op(Instruction.EXP);
        public static Prepare LT(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.LT);
        public static Prepare GT(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.GT);
        public static Prepare SLT(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.SLT);
        public static Prepare SGT(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.SGT);
        public static Prepare EQ(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.EQ);
        public static Prepare AND(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.AND);
        public static Prepare OR(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.OR);
        public static Prepare XOR(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.XOR);
        public static Prepare BYTE(this Prepare @this, UInt256? pos, byte[] bytes = null)
            => @this.PushSingle(bytes)
                    .PushSingle(pos)
                    .Op(Instruction.BYTE);
        public static Prepare SHA3(this Prepare @this, UInt256? pos = null, UInt256? len = null)
            => @this.PushSequence(len, pos)
                    .Op(Instruction.SHA3);
        public static Prepare SHL(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.SHL);
        public static Prepare SHR(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence(rhs, lhs)
                    .Op(Instruction.SHR);
        public static Prepare SAR(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null)
            => @this.PushSequence((UInt256?)rhs, lhs)
                    .Op(Instruction.SAR);
        public static Prepare MSTORE(this Prepare @this, UInt256? pos = null, byte[] data = null)
            => @this.PushSingle(data)
                    .PushSingle(pos)
                    .Op(Instruction.MSTORE);
        public static Prepare MSTORE8(this Prepare @this, UInt256? pos = null, byte[] data = null)
            => @this.PushSingle(data)
                    .PushSingle(pos)
                    .Op(Instruction.MSTORE8);
        public static Prepare TSTORE(this Prepare @this, UInt256? pos = null, byte[] data = null)
            => @this.PushSingle(data)
                    .PushSingle(pos)
                    .Op(Instruction.TSTORE);
        public static Prepare SSTORE(this Prepare @this, UInt256? pos = null, byte[] data = null)
            => @this.PushSingle(data)
                    .PushSingle(pos)
                    .Op(Instruction.SSTORE);
        public static Prepare JUMPI(this Prepare @this, UInt256? to = null, byte[] cond = null)
            => @this.PushSingle(cond)
                    .PushSingle(to)
                    .Op(Instruction.JUMPI);
        public static Prepare LOGx(this Prepare @this, byte i, UInt256? pos = null, UInt256? len = null)
            => @this.PushSequence(len, pos)
                    .Op(Instruction.LOG0 + i);
        public static Prepare RETURN(this Prepare @this, UInt256? pos = null, UInt256? len = null)
            => @this.PushSequence(len, pos)
                    .Op(Instruction.RETURN);
        public static Prepare RETURN(this Prepare @this, UInt256? pos, byte[] data)
            => @this.MSTORE8(pos, data)
                    .PushSequence((UInt256)data.Length, pos)
                    .Op(Instruction.RETURN);

        public static Prepare REVERT(this Prepare @this, UInt256? pos = null, UInt256? len = null)
            => @this.PushSequence(len, pos)
                    .Op(Instruction.REVERT);
        #endregion

        #region opcodes_with_3_args
        public static Prepare ADDMOD(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null, UInt256? mod = null)
            => @this.PushSequence(mod, rhs, lhs)
                    .Op(Instruction.ADDMOD);
        public static Prepare MULMOD(this Prepare @this, UInt256? lhs = null, UInt256? rhs = null, UInt256? mod = null)
            => @this.PushSequence(mod, rhs, lhs)
                    .Op(Instruction.MULMOD);
        public static Prepare CREATEx(this Prepare @this, byte i, UInt256? value = null, UInt256? initCodePos = null, UInt256? initCodeLen = null)
            => @this.PushSequence(initCodeLen, initCodePos, value)
                    .Op(i == 2 ? Instruction.CREATE2 : Instruction.CREATE);
        public static Prepare CALLDATACOPY(this Prepare @this, UInt256? dest = null, UInt256? src = null, UInt256? len = null)
            => @this.PushSequence(len, src, dest)
                    .Op(Instruction.CALLDATACOPY);
        public static Prepare CODECOPY(this Prepare @this, UInt256? dest = null, UInt256? src = null, UInt256? len = null)
            => @this.PushSequence(len, src, dest)
                    .Op(Instruction.CODECOPY);

        public static Prepare RETURNDATACOPY(this Prepare @this, UInt256? dest = null, UInt256? src = null, UInt256? len = null)
            => @this.PushSequence(len, src, dest)
                    .Op(Instruction.RETURNDATACOPY);
        #endregion

        #region opcodes_with_4+_args
        public static Prepare CALL(this Prepare @this, UInt256? gasLim = null,
                                        Address? codeSrc = null, UInt256? callValue = null,
                                        UInt256? dataOffset = null, UInt256? dataLength = null,
                                        UInt256? outputOffset = null, UInt256? outputLength = null)
            => @this.PushSequence(outputLength, outputOffset, dataLength, dataOffset, callValue)
                    .PushSingle(codeSrc)
                    .PushSingle(gasLim)
                    .Op(Instruction.CALL);
        public static Prepare CALLCODE(this Prepare @this, UInt256? gasLim = null,
                                            Address? codeSrc = null, UInt256? callValue = null,
                                            UInt256? dataOffset = null, UInt256? dataLength = null,
                                            UInt256? outputOffset = null, UInt256? outputLength = null)
            => @this.PushSequence(outputLength, outputOffset, dataLength, dataOffset, callValue)
                    .PushSingle(codeSrc)
                    .PushSingle(gasLim)
                    .Op(Instruction.CALLCODE);
        public static Prepare DELEGATECODE(this Prepare @this, UInt256? gasLim = null,
                                                Address? codeSrc = null, UInt256? dataOffset = null,
                                                UInt256? dataLength = null, UInt256? outputOffset = null,
                                                UInt256? outputLength = null)
            => @this.PushSequence(outputLength, outputOffset, dataLength, dataOffset)
                    .PushSingle(codeSrc)
                    .PushSingle(gasLim)
                    .Op(Instruction.DELEGATECALL);
        public static Prepare STATICCALL(this Prepare @this, UInt256? gasLim = null,
                                                Address? codeSrc = null, UInt256? dataOffset = null,
                                                UInt256? dataLength = null, UInt256? outputOffset = null,
                                                UInt256? outputLength = null)
            => @this.PushSequence(outputLength, outputOffset, dataLength, dataOffset)
                    .PushSingle(codeSrc)
                    .PushSingle(gasLim)
                    .Op(Instruction.STATICCALL);
        public static Prepare EXTCODECOPY(this Prepare @this, Address? codeSrc, UInt256? dest = null, UInt256? src = null, UInt256? len = null)
            => @this.PushSequence(len, src, dest)
                    .PushSingle(codeSrc)
                    .Op(Instruction.EXTCODECOPY);
        #endregion

        #region opcodes_with_immediates
        public static Prepare CALLF(this Prepare @this, UInt16 sectionId)
            => @this.Op(Instruction.CALLF)
                .Data(BitConverter.GetBytes(sectionId).Reverse().ToArray());
        public static Prepare CALLF(this Prepare @this, UInt16 sectionId, params byte[] arguments)
            => @this.PushData(arguments)
                    .Op(Instruction.CALLF)
                    .Data(BitConverter.GetBytes(sectionId));
        public static Prepare PUSHx(this Prepare @this, byte[] args)
            => @this.PushData(args);
        public static Prepare RJUMPI(this Prepare @this, Int16 to, byte[] cond = null)
            => @this.PushSingle(cond)
                        .Op(Instruction.RJUMPI)
                        .Data(BitConverter.GetBytes(to).Reverse().ToArray());
        #endregion
    }
}
