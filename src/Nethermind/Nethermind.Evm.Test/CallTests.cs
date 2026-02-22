// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Stack_underflow_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("StackUnderflow"));
        }

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Out_of_gas_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("OutOfGas"));
        }

        [Test]
        [TestCase(0)]
        [TestCase(32)]
        [TestCase(128)]
        public void Identity_precompile_call_returns_correct_data(int inputSize)
        {
            byte[] input = new byte[inputSize];
            for (int i = 0; i < inputSize; i++) input[i] = (byte)((i + 1) % 256);

            Prepare code = Prepare.EvmCode;
            if (inputSize > 0)
                code.StoreDataInMemory(0, input);

            // CALL identity: gas=50000, addr=0x04, value=0, argsOffset=0, argsSize=inputSize,
            // retOffset=0x200, retSize=inputSize.
            int retOffset = inputSize > 0 ? 0x200 : 0;
            code.PushData(inputSize)
                .PushData(retOffset)
                .PushData(inputSize)
                .PushData(0)
                .PushData(0)
                .PushData(IdentityPrecompile.Address)
                .PushData(50000)
                .Op(Instruction.CALL);

            // Store (CALL result + 100) in storage[0] to avoid zero-storage ambiguity.
            code.PushData(100)
                .Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE);

            // Store (RETURNDATASIZE + 100) in storage[1].
            code.Op(Instruction.RETURNDATASIZE)
                .PushData(100)
                .Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE);

            // Store first 32 bytes of output in storage[2].
            if (inputSize >= 32)
            {
                code.PushData(retOffset).Op(Instruction.MLOAD)
                    .PushData(2).Op(Instruction.SSTORE);
            }

            TestAllTracerWithOutput result = Execute(code.Done);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));

            // 1 + 100 = 101 proves CALL succeeded.
            AssertStorage((UInt256)0, (UInt256)101);
            // returnDataSize + 100.
            AssertStorage((UInt256)1, (UInt256)(inputSize + 100));

            if (inputSize >= 32)
            {
                byte[] expectedWord = new byte[32];
                Array.Copy(input, 0, expectedWord, 0, 32);
                AssertStorage((UInt256)2, new ReadOnlySpan<byte>(expectedWord));
            }
        }

        [Test]
        public void Identity_precompile_call_with_insufficient_gas_returns_failure()
        {
            byte[] input = new byte[32];
            for (int i = 0; i < 32; i++) input[i] = (byte)(i + 1);

            // Forward 10 gas to identity precompile (needs 18: 15 base + 3 per word).
            byte[] code = Prepare.EvmCode
                .CallWithInput(IdentityPrecompile.Address, 10L, input)
                .PushData(7)
                .Op(Instruction.ADD)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            // 0 + 7 = 7 proves CALL returned 0 (failure).
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        [TestCase(0)]
        [TestCase(32)]
        public void Identity_precompile_staticcall_succeeds(int inputSize)
        {
            byte[] input = new byte[inputSize];
            for (int i = 0; i < inputSize; i++) input[i] = (byte)((i + 1) % 256);

            byte[] code = Prepare.EvmCode
                .DynamicCallWithInput(Instruction.STATICCALL, IdentityPrecompile.Address, 50000L, input)
                .PushData(100)
                .Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100)
                .Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)(inputSize + 100));
        }
        // ===== EcRecover (0x01) =====

        [Test]
        public void Ecrecover_precompile_call_returns_success_with_empty_output()
        {
            // EcRecover with all-zero 128-byte input: v=0 (not 27/28), returns empty data.
            byte[] code = Prepare.EvmCode
                .CallWithInput(EcRecoverPrecompile.Address, 50000L, new byte[128])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101); // CALL succeeded
            AssertStorage((UInt256)1, (UInt256)100);  // RETURNDATASIZE = 0
        }

        [Test]
        public void Ecrecover_precompile_call_with_insufficient_gas_returns_failure()
        {
            // EcRecover needs 3000 gas; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(EcRecoverPrecompile.Address, 10L, new byte[128])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7); // CALL returned 0 (failure)
        }

        // ===== Sha256 (0x02) =====

        [Test]
        [TestCase(0, 32)]
        [TestCase(32, 32)]
        public void Sha256_precompile_call_returns_correct_data(int inputSize, int expectedReturnSize)
        {
            byte[] input = new byte[inputSize];
            for (int i = 0; i < inputSize; i++) input[i] = (byte)((i + 1) % 256);

            byte[] code = Prepare.EvmCode
                .CallWithInput(Sha256Precompile.Address, 50000L, input)
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)(expectedReturnSize + 100));
        }

        [Test]
        public void Sha256_precompile_call_with_insufficient_gas_returns_failure()
        {
            // Sha256 needs 60 + 12 = 72 gas for 32-byte input; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(Sha256Precompile.Address, 10L, new byte[32])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        public void Sha256_precompile_staticcall_succeeds()
        {
            byte[] code = Prepare.EvmCode
                .DynamicCallWithInput(Instruction.STATICCALL, Sha256Precompile.Address, 50000L, new byte[32])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)132); // 32 + 100
        }

        // ===== Ripemd160 (0x03) =====

        [Test]
        [TestCase(0, 32)]
        [TestCase(32, 32)]
        public void Ripemd160_precompile_call_returns_correct_data(int inputSize, int expectedReturnSize)
        {
            byte[] input = new byte[inputSize];
            for (int i = 0; i < inputSize; i++) input[i] = (byte)((i + 1) % 256);

            byte[] code = Prepare.EvmCode
                .CallWithInput(Ripemd160Precompile.Address, 50000L, input)
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)(expectedReturnSize + 100));
        }

        [Test]
        public void Ripemd160_precompile_call_with_insufficient_gas_returns_failure()
        {
            // Ripemd160 needs 600 + 120 = 720 gas for 32-byte input; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(Ripemd160Precompile.Address, 10L, new byte[32])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== ModExp (0x05) =====

        [Test]
        public void Modexp_precompile_call_returns_correct_data()
        {
            // Compute 2^3 mod 5 = 3. Input: base_len=1, exp_len=1, mod_len=1, base=2, exp=3, mod=5.
            byte[] modExpInput = new byte[99];
            modExpInput[31] = 1;  // base_length = 1
            modExpInput[63] = 1;  // exp_length = 1
            modExpInput[95] = 1;  // mod_length = 1
            modExpInput[96] = 2;  // base = 2
            modExpInput[97] = 3;  // exp = 3
            modExpInput[98] = 5;  // mod = 5

            byte[] code = Prepare.EvmCode
                .CallWithInput(ModExpPrecompile.Address, 50000L, modExpInput)
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101); // CALL succeeded
            AssertStorage((UInt256)1, (UInt256)101);  // RETURNDATASIZE = 1 (mod_length)
        }

        [Test]
        public void Modexp_precompile_call_with_insufficient_gas_returns_failure()
        {
            byte[] modExpInput = new byte[99];
            modExpInput[31] = 1;
            modExpInput[63] = 1;
            modExpInput[95] = 1;
            modExpInput[96] = 2;
            modExpInput[97] = 3;
            modExpInput[98] = 5;

            // ModExp minimum gas is 200 (EIP-2565); forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(ModExpPrecompile.Address, 10L, modExpInput)
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== BN254Add (0x06) =====

        [Test]
        public void Bn254add_precompile_call_returns_correct_data()
        {
            // Two zero G1 points (128 bytes) → returns zero point (64 bytes).
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254AddPrecompile.Address, 50000L, new byte[128])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)164); // 64 + 100
        }

        [Test]
        public void Bn254add_precompile_call_with_insufficient_gas_returns_failure()
        {
            // BN254Add needs 150 gas (post-EIP-1108); forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254AddPrecompile.Address, 10L, new byte[128])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        public void Bn254add_precompile_staticcall_succeeds()
        {
            byte[] code = Prepare.EvmCode
                .DynamicCallWithInput(Instruction.STATICCALL, BN254AddPrecompile.Address, 50000L, new byte[128])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)164);
        }

        // ===== BN254Mul (0x07) =====

        [Test]
        public void Bn254mul_precompile_call_returns_correct_data()
        {
            // Zero G1 point × zero scalar (96 bytes) → zero point (64 bytes).
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254MulPrecompile.Address, 50000L, new byte[96])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)164); // 64 + 100
        }

        [Test]
        public void Bn254mul_precompile_call_with_insufficient_gas_returns_failure()
        {
            // BN254Mul needs 6000 gas (post-EIP-1108); forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254MulPrecompile.Address, 10L, new byte[96])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== BN254Pairing (0x08) =====

        [Test]
        public void Bn254pairing_precompile_call_with_empty_input_succeeds()
        {
            // Empty input is valid: vacuously true pairing, returns 32-byte result (value 1).
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254PairingPrecompile.Address, 50000L, Array.Empty<byte>())
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 500000L, code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)132); // 32 + 100
        }

        [Test]
        public void Bn254pairing_precompile_call_with_invalid_length_returns_failure()
        {
            // Input length (100) is not a multiple of 192 → precompile returns failure.
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254PairingPrecompile.Address, 200000L, new byte[100])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 1000000L, code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7); // CALL returned 0 (failure)
        }

        [Test]
        public void Bn254pairing_precompile_call_with_insufficient_gas_returns_failure()
        {
            // BN254Pairing needs 45000 gas base; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(BN254PairingPrecompile.Address, 10L, Array.Empty<byte>())
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== Blake2F (0x09) =====

        [Test]
        public void Blake2f_precompile_call_returns_correct_data()
        {
            // 213-byte input: 1 round, all-zero state, final flag = 1 → returns 64 bytes.
            byte[] blake2fInput = new byte[213];
            blake2fInput[3] = 1;    // rounds = 1 (big-endian uint32)
            blake2fInput[212] = 1;  // final flag = 1

            byte[] code = Prepare.EvmCode
                .CallWithInput(Blake2FPrecompile.Address, 50000L, blake2fInput)
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)164); // 64 + 100
        }

        [Test]
        public void Blake2f_precompile_call_with_invalid_input_length_returns_failure()
        {
            // Input length (100) != 213 → precompile returns failure.
            byte[] code = Prepare.EvmCode
                .CallWithInput(Blake2FPrecompile.Address, 50000L, new byte[100])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        public void Blake2f_precompile_call_with_invalid_final_flag_returns_failure()
        {
            // 213-byte input with final flag = 2 (invalid, must be 0 or 1) → failure.
            byte[] blake2fInput = new byte[213];
            blake2fInput[3] = 1;
            blake2fInput[212] = 2;  // invalid final flag

            byte[] code = Prepare.EvmCode
                .CallWithInput(Blake2FPrecompile.Address, 50000L, blake2fInput)
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        public void Blake2f_precompile_call_with_insufficient_gas_returns_failure()
        {
            // Blake2F with 10 rounds needs 10 gas; forward only 1.
            byte[] blake2fInput = new byte[213];
            blake2fInput[3] = 10;   // rounds = 10
            blake2fInput[212] = 1;

            byte[] code = Prepare.EvmCode
                .CallWithInput(Blake2FPrecompile.Address, 1L, blake2fInput)
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== BLS G1Add (0x0b) =====

        [Test]
        public void Bls_g1add_precompile_call_returns_correct_data()
        {
            // Two zero (infinity) G1 points (2 × 128 = 256 bytes) → returns infinity point (128 bytes).
            byte[] code = Prepare.EvmCode
                .CallWithInput(G1AddPrecompile.Address, 50000L, new byte[256])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)228); // 128 + 100
        }

        [Test]
        public void Bls_g1add_precompile_call_with_invalid_input_length_returns_failure()
        {
            // G1Add expects exactly 256 bytes; 100 bytes → failure.
            byte[] code = Prepare.EvmCode
                .CallWithInput(G1AddPrecompile.Address, 50000L, new byte[100])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        [Test]
        public void Bls_g1add_precompile_call_with_insufficient_gas_returns_failure()
        {
            // G1Add needs 375 gas; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(G1AddPrecompile.Address, 10L, new byte[256])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== BLS G2Add (0x0d) =====

        [Test]
        public void Bls_g2add_precompile_call_returns_correct_data()
        {
            // Two zero (infinity) G2 points (2 × 256 = 512 bytes) → returns infinity point (256 bytes).
            byte[] code = Prepare.EvmCode
                .CallWithInput(G2AddPrecompile.Address, 50000L, new byte[512])
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)101);
            AssertStorage((UInt256)1, (UInt256)356); // 256 + 100
        }

        [Test]
        public void Bls_g2add_precompile_call_with_insufficient_gas_returns_failure()
        {
            // G2Add needs 600 gas; forward only 10.
            byte[] code = Prepare.EvmCode
                .CallWithInput(G2AddPrecompile.Address, 10L, new byte[512])
                .PushData(7).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (UInt256)7);
        }

        // ===== Fast-path vs non-fast-path parity tests =====
        //
        // These tests ensure the fast precompile path (used when IsTracingActions=false)
        // and the normal frame-based path (used when IsTracingActions=true) produce
        // identical results: same gas consumption, same return data, and same status code.

        /// <summary>
        /// Builds bytecode that calls a precompile and stores diagnostic values in storage:
        ///   slot 0: CALL result + 100
        ///   slot 1: RETURNDATASIZE + 100
        ///   slot 2: GAS before call - GAS after call (net gas consumed by the call)
        /// </summary>
        private static byte[] BuildPrecompileParityCode(Address precompileAddress, long forwardedGas, byte[] input)
        {
            Prepare code = Prepare.EvmCode;
            if (input.Length > 0)
                code.StoreDataInMemory(0, input);

            // GAS before call
            code.Op(Instruction.GAS);

            // CALL precompile
            code.PushData(0)                      // retSize
                .PushData(0)                       // retOffset
                .PushData(input.Length)             // argsSize
                .PushData(0)                       // argsOffset
                .PushData(0)                       // value
                .PushData(precompileAddress)
                .PushData(forwardedGas)
                .Op(Instruction.CALL);

            // stack: [gasBefore, callResult]

            // Store CALL result + 100 in slot 0
            code.PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE);

            // GAS after call; compute gasBefore - gasAfter
            code.Op(Instruction.GAS)
                .Op(Instruction.SWAP1)  // stack: [gasAfter, gasBefore]
                .Op(Instruction.SUB)    // gasBefore - gasAfter
                .PushData(2).Op(Instruction.SSTORE);

            // Store RETURNDATASIZE + 100 in slot 1
            code.Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE);

            return code.Done;
        }

        /// <summary>
        /// Executes the same precompile call under both tracing modes:
        /// - Non-fast path: default tracer (IsTracingActions=true)
        /// - Fast path: tracer with IsTracingActions=false
        /// Asserts gas usage, return data size, and status code are identical.
        /// </summary>
        private void AssertFastPathParity(Address precompileAddress, long forwardedGas, byte[] input, long txGasLimit = 200000L)
        {
            byte[] code = BuildPrecompileParityCode(precompileAddress, forwardedGas, input);

            // Run 1: non-fast path (IsTracingActions=true via default tracer)
            TestAllTracerWithOutput nonFastResult = Execute(Activation, txGasLimit, code);

            byte[] slot0NonFast = TestState.Get(new StorageCell(Recipient, 0)).ToArray();
            byte[] slot1NonFast = TestState.Get(new StorageCell(Recipient, 1)).ToArray();
            byte[] slot2NonFast = TestState.Get(new StorageCell(Recipient, 2)).ToArray();
            long gasNonFast = nonFastResult.GasSpent;
            byte statusNonFast = nonFastResult.StatusCode;

            // Reset state for second run
            TearDown();
            Setup();

            // Run 2: fast path (IsTracingActions=false)
            (Block block, Transaction transaction) = PrepareTx(Activation, txGasLimit, code);
            FastPathTracerWithOutput fastResult = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), fastResult);

            byte[] slot0Fast = TestState.Get(new StorageCell(Recipient, 0)).ToArray();
            byte[] slot1Fast = TestState.Get(new StorageCell(Recipient, 1)).ToArray();
            byte[] slot2Fast = TestState.Get(new StorageCell(Recipient, 2)).ToArray();

            // Assert parity
            Assert.Multiple(() =>
            {
                Assert.That(statusNonFast, Is.EqualTo(StatusCode.Success), "non-fast path should succeed");
                Assert.That(fastResult.StatusCode, Is.EqualTo(statusNonFast), "status code mismatch between fast and non-fast paths");
                Assert.That(slot0Fast, Is.EqualTo(slot0NonFast), "CALL result mismatch (slot 0)");
                Assert.That(slot1Fast, Is.EqualTo(slot1NonFast), "RETURNDATASIZE mismatch (slot 1)");
                Assert.That(slot2Fast, Is.EqualTo(slot2NonFast), "gas consumed by CALL mismatch (slot 2)");
                Assert.That(fastResult.GasSpent, Is.EqualTo(gasNonFast), "total transaction gas mismatch");
            });
        }

        private static IEnumerable<TestCaseData> PrecompileParityCases()
        {
            // Identity
            yield return new TestCaseData(IdentityPrecompile.Address, 50000L, Array.Empty<byte>())
                .SetName("identity_empty_input");
            byte[] sequentialInput = new byte[32];
            for (int i = 0; i < 32; i++) sequentialInput[i] = (byte)(i + 1);
            yield return new TestCaseData(IdentityPrecompile.Address, 50000L, sequentialInput)
                .SetName("identity_32_bytes");
            yield return new TestCaseData(IdentityPrecompile.Address, 10L, new byte[32])
                .SetName("identity_insufficient_gas");

            // SHA-256
            yield return new TestCaseData(Sha256Precompile.Address, 50000L, new byte[32])
                .SetName("sha256_32_bytes");
            yield return new TestCaseData(Sha256Precompile.Address, 10L, new byte[32])
                .SetName("sha256_insufficient_gas");

            // ModExp: base=2, exp=3, mod=5
            byte[] modExpInput = new byte[99];
            modExpInput[31] = 1; modExpInput[63] = 1; modExpInput[95] = 1;
            modExpInput[96] = 2; modExpInput[97] = 3; modExpInput[98] = 5;
            yield return new TestCaseData(ModExpPrecompile.Address, 50000L, modExpInput)
                .SetName("modexp");

            // BN254
            yield return new TestCaseData(BN254AddPrecompile.Address, 50000L, new byte[128])
                .SetName("bn254add");
            yield return new TestCaseData(BN254AddPrecompile.Address, 10L, new byte[128])
                .SetName("bn254add_insufficient_gas");
            yield return new TestCaseData(BN254MulPrecompile.Address, 50000L, new byte[96])
                .SetName("bn254mul");
            yield return new TestCaseData(BN254PairingPrecompile.Address, 50000L, Array.Empty<byte>())
                .SetName("bn254pairing_empty_input");

            // Blake2f: rounds=1, final=1
            byte[] blake2fValid = new byte[213];
            blake2fValid[3] = 1; blake2fValid[212] = 1;
            yield return new TestCaseData(Blake2FPrecompile.Address, 50000L, blake2fValid)
                .SetName("blake2f");

            // Blake2f with invalid final flag
            byte[] blake2fInvalid = new byte[213];
            blake2fInvalid[3] = 1; blake2fInvalid[212] = 2;
            yield return new TestCaseData(Blake2FPrecompile.Address, 50000L, blake2fInvalid)
                .SetName("blake2f_invalid_input");

            // BLS
            yield return new TestCaseData(G1AddPrecompile.Address, 50000L, new byte[256])
                .SetName("bls_g1add");
            yield return new TestCaseData(G2AddPrecompile.Address, 50000L, new byte[512])
                .SetName("bls_g2add");
        }

        [TestCaseSource(nameof(PrecompileParityCases))]
        public void Fast_path_precompile_parity(Address precompileAddress, long forwardedGas, byte[] input)
        {
            AssertFastPathParity(precompileAddress, forwardedGas, input);
        }

        [Test]
        public void Fast_path_parity_delegatecall_to_identity()
        {
            byte[] input = new byte[32];
            for (int i = 0; i < 32; i++) input[i] = (byte)(i + 1);

            // Build code that uses DELEGATECALL to identity precompile.
            byte[] codeNonFast = Prepare.EvmCode
                .StoreDataInMemory(0, input)
                .DynamicCallWithInput(Instruction.DELEGATECALL, IdentityPrecompile.Address, 50000L, input)
                .PushData(100).Op(Instruction.ADD)
                .PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.RETURNDATASIZE)
                .PushData(100).Op(Instruction.ADD)
                .PushData(1).Op(Instruction.SSTORE)
                .Done;

            // Run non-fast path
            TestAllTracerWithOutput nonFastResult = Execute(Activation, 200000L, codeNonFast);
            byte[] slot0NonFast = TestState.Get(new StorageCell(Recipient, 0)).ToArray();
            byte[] slot1NonFast = TestState.Get(new StorageCell(Recipient, 1)).ToArray();

            TearDown();
            Setup();

            // Run fast path with same code
            (Block block, Transaction transaction) = PrepareTx(Activation, 200000L, codeNonFast);
            FastPathTracerWithOutput fastResult = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), fastResult);
            byte[] slot0Fast = TestState.Get(new StorageCell(Recipient, 0)).ToArray();
            byte[] slot1Fast = TestState.Get(new StorageCell(Recipient, 1)).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(fastResult.StatusCode, Is.EqualTo(nonFastResult.StatusCode), "status code mismatch");
                Assert.That(slot0Fast, Is.EqualTo(slot0NonFast), "CALL result mismatch (slot 0)");
                Assert.That(slot1Fast, Is.EqualTo(slot1NonFast), "RETURNDATASIZE mismatch (slot 1)");
                Assert.That(fastResult.GasSpent, Is.EqualTo(nonFastResult.GasSpent), "gas mismatch");
            });
        }
    }

    /// <summary>
    /// Tracer identical to <see cref="TestAllTracerWithOutput"/> but with
    /// <see cref="IsTracingActions"/> disabled, allowing the precompile fast path to activate.
    /// </summary>
    internal class FastPathTracerWithOutput : TestAllTracerWithOutput
    {
        public override bool IsTracingActions => false;
    }
}
