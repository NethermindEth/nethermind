using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class VirtualMachine
    {
        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly byte[] P255 = P255Int.ToBigEndianByteArray();
        private static readonly byte[] P0 = BigInteger.Zero.ToBigEndianByteArray();
        private static readonly byte[] P1 = BigInteger.One.ToBigEndianByteArray();
        private readonly int _programCounter = 0;

        private readonly EvmStack _stack = new EvmStack();

        private EvmMemory _memory = new EvmMemory();

        private int _stackLocation = 0;

        private EvmStore _store = new EvmStore();

        private int _storeLocation = 0;

        public byte[] Run(ExecutionEnvironment executionEnvironment)
        {
            byte[] output = null;
            byte[] code = executionEnvironment.MachineCode;
            while (true)
            {
                bool stopExecution = false;

                Instruction instruction = (Instruction) executionEnvironment.MachineCode[_programCounter];
                if (instruction == Instruction.STOP)
                {
                    break;
                }

                BigInteger reg1;
                BigInteger reg2;
                BigInteger reg3;

                byte[] byte1;
                byte[] byte2;

                // TODO: must be in P256
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        stopExecution = true;
                        break;
                    }
                    case Instruction.ADD:
                    {
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push((reg1 + reg2).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.MUL:
                    {
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push((reg1 * reg2).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.DIV:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(reg2 == BigInteger.Zero ? P0 : BigInteger.Divide(reg1, reg2).ToBigEndianByteArray());
                        break;
                    case Instruction.SDIV:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        reg2 = _stack.Pop().ToSignedBigInteger();
                        if (reg2 == BigInteger.Zero)
                        {
                            _stack.Push(P0);
                        }
                        else if (reg2 == -1 && reg1 == P255Int)
                        {
                            _stack.Push(P255);
                        }
                        else
                        {
                            _stack.Push(BigInteger.Divide(reg1, reg2).ToBigEndianByteArray());
                        }
                        break;
                    case Instruction.MOD:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(reg2 == BigInteger.Zero ? P0 : BigInteger.Remainder(reg1, reg2).ToBigEndianByteArray());
                        break;
                    case Instruction.SMOD:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        reg2 = _stack.Pop().ToSignedBigInteger();
                        _stack.Push(
                            reg2 == BigInteger.Zero
                                ? P0
                                : (reg1.Sign * BigInteger.Remainder(reg1.Abs(), reg2.Abs())).ToBigEndianByteArray());
                        break;
                    case Instruction.ADDMOD:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        reg3 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(reg2.ToBigEndianByteArray());
                        _stack.Push(
                            reg3 == BigInteger.Zero
                            ? P0 : BigInteger.Remainder(reg1 + reg2, reg3).ToBigEndianByteArray());
                        break;
                    case Instruction.MULMOD:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        reg3 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(reg2.ToBigEndianByteArray());
                        _stack.Push(
                            reg3 == BigInteger.Zero
                                ? P0 : BigInteger.Remainder(reg1 * reg2, reg3).ToBigEndianByteArray());
                        break;
                    case Instruction.EXP:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        if (reg1 == 0)
                        {
                            _stack.Push(P0);
                        }
                        else if (reg1 == 1)
                        {
                            _stack.Push(P1);
                        }
                        else
                        {
                            _stack.Push(BigInteger.Pow(reg1, (int) reg2).ToBigEndianByteArray()); // how do we protect against calls with reg2 value huge?
                            // there is Microsoft.SolverFoundation.Common
                        }
                        break;
                    case Instruction.SIGNEXTEND:
                        byte1 = _stack.Pop();
                        byte2 = _stack.Pop();
                        throw new NotImplementedException();
                    case Instruction.LT:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(BigInteger.Compare(reg1, reg2) < 0 ? reg1.ToBigEndianByteArray() : reg2.ToBigEndianByteArray());
                        break;
                    case Instruction.GT:
                        reg1 = _stack.Pop().ToUnsignedBigInteger();
                        reg2 = _stack.Pop().ToUnsignedBigInteger();
                        _stack.Push(BigInteger.Compare(reg1, reg2) > 0 ? reg1.ToBigEndianByteArray() : reg2.ToBigEndianByteArray());
                        break;
                    case Instruction.SLT:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        reg2 = _stack.Pop().ToSignedBigInteger();
                        _stack.Push(BigInteger.Compare(reg1, reg2) < 0 ? P1 : P0);
                        break;
                    case Instruction.SGT:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        reg2 = _stack.Pop().ToSignedBigInteger();
                        _stack.Push(BigInteger.Compare(reg1, reg2) > 0 ? P1 : P0);
                        break;
                    case Instruction.EQ:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        reg2 = _stack.Pop().ToSignedBigInteger();
                        _stack.Push(reg1 == reg2 ? P1 : P0);
                        break;
                    case Instruction.ISZERO:
                        reg1 = _stack.Pop().ToSignedBigInteger();
                        _stack.Push(reg1 == 0 ? P1 : P0);
                        break;
                    case Instruction.AND:
                        byte1 = _stack.Pop();
                        byte2 = _stack.Pop();
                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte) (byte1[i] & byte2[i]);
                        }

                        _stack.Push(byte1);
                        break;
                    case Instruction.OR:
                        byte1 = _stack.Pop();
                        byte2 = _stack.Pop();
                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)(byte1[i] | byte2[i]);
                        }

                        _stack.Push(byte1);
                        break;
                    case Instruction.XOR:
                        byte1 = _stack.Pop();
                        byte2 = _stack.Pop();
                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)(byte1[i] % byte2[i]);
                        }

                        _stack.Push(byte1);
                        break;
                    case Instruction.NOT:
                        byte1 = _stack.Pop();
                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)~byte1[i];
                        }
                        _stack.Push(byte1);
                        break;
                    case Instruction.BYTE:
                        throw new NotImplementedException();
                    case Instruction.SHA3:
                        throw new NotImplementedException();
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (stopExecution)
                {
                    break;
                }
            }

            return output;
        }
    }
}