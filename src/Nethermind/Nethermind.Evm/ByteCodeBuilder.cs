// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    /// <summary>
    /// A utility class to easily construct common patterns of EVM byte code
    /// </summary>
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

        public Prepare Op(Instruction instruction)
        {
            _byteCode.Add((byte)instruction);
            return this;
        }

        public Prepare Create(byte[] code, in UInt256 value)
        {
            StoreDataInMemory(0, code);
            PushData(code.Length);
            PushData(0);
            PushData(value);
            Op(Instruction.CREATE);
            return this;
        }

        public Prepare Create2(byte[] code, byte[] salt, in UInt256 value)
        {
            StoreDataInMemory(0, code);
            PushData(salt);
            PushData(code.Length);
            PushData(0);
            PushData(value);
            Op(Instruction.CREATE2);
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

        public Prepare CallWithInput(Address address, long gasLimit, byte[]? input = null)
        {
            if (input is not null)
            {
                StoreDataInMemory(0, input);
            }
            else
            {
                // Use top of stack as input
                DataOnStackToMemory(0);
            }
            PushData(0);
            PushData(0);
            PushData(input is not null ? input.Length : 32);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(Instruction.CALL);
            return this;
        }

        public Prepare CallWithValue(Address address, long gasLimit, in UInt256 value)
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

        public Prepare CallCode(Address address, long gasLimit, UInt256? transferValue = null, UInt256? dataOffset = null)
        {
            PushData(0);
            PushData(0);
            PushData(0);
            PushData(dataOffset ?? UInt256.Zero);
            PushData(transferValue ?? UInt256.Zero);
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

        /// <summary>
        /// Call the address with the specified callType
        /// </summary>
        /// <param name="callType">CALL, STATICCALL, DELEGATECALL</param>
        /// <param name="address">Address of the contract</param>
        /// <param name="gasLimit">Gas limit of the call</param>
        /// <param name="input">Optional 32 byte input</param>
        /// <returns>Prepare with call bytecode</returns>
        /// <exception cref="Exception">Throws exception if callType is incorrect</exception>
        public Prepare DynamicCallWithInput(Instruction callType, Address address, long gasLimit, byte[]? input = null)
        {
            if (callType != Instruction.CALL &&
                callType != Instruction.STATICCALL &&
                callType != Instruction.DELEGATECALL)
            {
                throw new Exception($"Unexpected call type {callType}");
            }
            if (input is not null)
            {
                StoreDataInMemory(0, input);
            }
            else
            {
                // Use top of stack as input
                DataOnStackToMemory(0);
            }
            PushData(0);
            PushData(0);
            PushData(input is not null ? input.Length : 32);
            PushData(0);
            PushData(0);
            PushData(address);
            PushData(gasLimit);
            Op(callType);
            return this;
        }

        public Prepare Log(int size, int position, Hash256[]? topics = null)
        {
            if (topics?.Length > 4)
            {
                throw new ArgumentException("Too many topics - must be 4 or less");
            }
            int numTopics = topics?.Length ?? 0;
            if (topics is not null)
            {
                foreach (Hash256 topic in topics)
                {
                    PushData(topic.Bytes.ToArray());
                }
            }
            PushData(size);
            PushData(position);
            Op(Instruction.LOG0 + (byte)numTopics);
            return this;
        }

        public Prepare Revert(int size, int position)
        {
            PushData(size);
            PushData(position);
            Op(Instruction.REVERT);
            return this;
        }

        public Prepare RevertWithSolidityErrorEncoding(string errorMessage)
        {
            // based on https://docs.soliditylang.org/en/latest/control-structures.html#revert
            // 0x08c379a0                                                         // Function selector for Error(string)
            // 0x0000000000000000000000000000000000000000000000000000000000000020 // Data offset
            // 0x000000000000000000000000000000000000000000000000000000000000001a // String length
            // 0x4e6f7420656e6f7567682045746865722070726f76696465642e000000000000 // String data

            byte[] errorMessageBytes = Encoding.UTF8.GetBytes(errorMessage);
            byte[] errorSelector = Bytes.FromHexString("0x08c379a0"); // Error(string) selector
            int paddedStringLength = ((errorMessageBytes.Length + 31) / 32) * 32;
            int totalLength = 4 + 32 + 32 + paddedStringLength;

            // Build the first 32 bytes: selector (4 bytes) + start of offset (28 bytes)
            // We want: 0x08c379a0 followed by 28 zero bytes
            // Need to shift left: 0x08c379a0 << 224 bits = 0x08c379a0000...000
            PushData(errorSelector);
            PushData(224);  // 28 bytes * 8 bits = 224 bits
            Op(Instruction.SHL);  // Shift left to position selector at bytes 0-3
            PushData(0);
            Op(Instruction.MSTORE);

            // Store offset to string data (32) at memory offset 4
            // This will occupy bytes 4-35
            PushData(32);
            PushData(4);
            Op(Instruction.MSTORE);

            // Store string length at memory offset 36
            // This will occupy bytes 36-67
            PushData(errorMessageBytes.Length);
            PushData(36);
            Op(Instruction.MSTORE);

            // Store actual string data at memory offset 68
            StoreDataInMemory(68, errorMessageBytes);

            // REVERT(offset=0, length=totalLength)
            PushData(totalLength);
            PushData(0);
            Op(Instruction.REVERT);
            return this;
        }

        public Prepare PushData(Address address)
        {
            PushData(address.Bytes);
            return this;
        }

        public Prepare PushData(int data)
        {
            return PushData((UInt256)data);
        }

        public Prepare PushData(long data)
        {
            return PushData((UInt256)data);
        }

        public Prepare PushData(in UInt256 data)
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

        public Prepare PushData(ReadOnlyMemory<byte> data)
        {
            _byteCode.Add((byte)(Instruction.PUSH1 + (byte)data.Length - 1));
            _byteCode.AddRange(data.Span);
            return this;
        }

        public Prepare PushData(byte data)
        {
            PushData(new[] { data });
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

        public Prepare StoreDataInMemory(int position, byte[] data)
        {
            for (int i = 0; i < data.Length; i += 32)
            {
                PushData(data.Slice(i, data.Length - i).PadRight(32));
                PushData(position + i);
                Op(Instruction.MSTORE);
            }

            return this;
        }

        /// <summary>
        /// Take the data already on stack and store it in memory
        /// at specified position
        /// </summary>
        /// <param name="position">Memory position</param>
        /// <returns>Prepare with requested bytecode</returns>
        public Prepare DataOnStackToMemory(int position)
        {
            PushData(position);
            Op(Instruction.MSTORE);
            return this;
        }

        /// <summary>
        /// Store input value at specified key in transient storage
        /// </summary>
        /// <param name="key">Storage key</param>
        /// <param name="value">Value to store</param>
        /// <returns>Prepare with requested bytecode</returns>
        public Prepare StoreDataInTransientStorage(int key, int value)
        {
            PushData(value);
            PushData(key);
            Op(Instruction.TSTORE);
            return this;
        }

        /// <summary>
        /// Load value from specified key in transient storage
        /// </summary>
        /// <param name="key">Storage key</param>
        /// <returns>Prepare with requested bytecode</returns>
        public Prepare LoadDataFromTransientStorage(int key)
        {
            PushData(key);
            Op(Instruction.TLOAD);
            return this;
        }

        /// <summary>
        /// Return the data in memory at position
        /// </summary>
        /// <param name="size">Data size</param>
        /// <param name="position">Memory position</param>
        /// <returns>Prepare with requested bytecode</returns>
        public Prepare Return(int size, int position)
        {
            PushData(size);
            PushData(position);
            Op(Instruction.RETURN);
            return this;
        }

        /// <summary>
        /// Returns the result from a call made immediately prior
        /// </summary>
        /// <returns>Prepare with requested bytecode</returns>
        public Prepare ReturnInnerCallResult()
        {
            PushData(32);
            PushData(0);
            PushData(0);
            Op(Instruction.RETURNDATACOPY);
            PushData(32);
            PushData(0);
            Op(Instruction.RETURN);
            return this;
        }
    }
}
