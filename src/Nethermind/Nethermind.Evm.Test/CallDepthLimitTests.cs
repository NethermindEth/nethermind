// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [Parallelizable(ParallelScope.Self)]
    public class CallDepthLimitTests : VirtualMachineTestsBase
    {
        [Test]
        public void Call_depth_limit_reached()
        {
            // Create a simple contract that calls itself recursively
            byte[] contractCode = new byte[]
            {
                // Call self with all gas
                (byte)Instruction.PUSH1, 0x00,         // No return data
                (byte)Instruction.PUSH1, 0x00,         // No return data size
                (byte)Instruction.PUSH1, 0x00,         // No input data
                (byte)Instruction.PUSH1, 0x00,         // No input data size
                (byte)Instruction.PUSH1, 0x00,         // No value
                (byte)Instruction.ADDRESS,             // This contract address
                (byte)Instruction.GAS,                 // All remaining gas
                (byte)Instruction.CALL,                // Call itself recursively

                // Store result in storage slot 0
                (byte)Instruction.PUSH1, 0x00,         // Storage slot 0
                (byte)Instruction.SSTORE,              // Store call result

                (byte)Instruction.STOP                 // Stop execution
            };

            // Make sure the account exists before inserting code
            if (!TestState.AccountExists(Recipient))
            {
                TestState.CreateAccount(Recipient, 100.Ether());
            }

            TestState.InsertCode(Recipient, Keccak.Compute(contractCode), contractCode, Spec);
            TestState.Commit(Spec);

            // Call the contract with enough gas for deep recursion
            long gasLimit = Spec.IsEip2565Enabled ? 5000000 : 900000;
            byte[] callCode = Prepare.EvmCode
                .Call(Recipient, gasLimit)
                .Done;

            TestAllTracerWithOutput receipt = Execute(gasLimit + 100000, callCode);
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "Transaction should succeed");

            byte[] storedValue = TestState.Get(new StorageCell(Recipient, 0)).ToArray();
            Assert.That(storedValue.Length, Is.GreaterThan(0), "Storage value should exist");
        }

        [Test]
        public void Call_fails_at_max_depth()
        {
            // Test contract: counter in slot 0, final call result in slot 1, depth of failure in slot 2
            byte[] contractCode = new byte[]
            {
                // Initialize counter to 0
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Loop start
                (byte)Instruction.JUMPDEST,

                // Load and increment counter
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.ADD,
                (byte)Instruction.DUP1,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Call self
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.ADDRESS,
                (byte)Instruction.PUSH3, 0x01, 0x86, 0xA0, // 100,000 gas
                (byte)Instruction.CALL,

                // Store call result in slot 1
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SSTORE,

                // Store current depth in slot 2
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x02,
                (byte)Instruction.SSTORE,

                // Check if call failed
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.EQ,
                (byte)Instruction.PUSH1, 0x3C, // Jump to the END
                (byte)Instruction.JUMPI,

                // Decrement counter for successful call
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SWAP1,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // END
                (byte)Instruction.JUMPDEST,
                (byte)Instruction.STOP
            };

            // Make sure the account exists before inserting code
            if (!TestState.AccountExists(Recipient))
            {
                TestState.CreateAccount(Recipient, 100.Ether());
            }

            TestState.InsertCode(Recipient, Keccak.Compute(contractCode), contractCode, Spec);
            TestState.Commit(Spec);

            // Call the contract with enough gas
            // EIP-2565 requires more gas as the ModExp operation is more expensive
            long gasLimit = Spec.IsEip2565Enabled ? 50000000 : 10000000;
            byte[] callCode = Prepare.EvmCode
                .Call(Recipient, gasLimit)
                .Done;

            TestAllTracerWithOutput receipt = Execute(gasLimit + 1000000, callCode);
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "Transaction should succeed");

            // Check storage slot 1 to see if a call failed
            byte[] successFlag = TestState.Get(new StorageCell(Recipient, 1)).ToArray();
            Assert.That(successFlag.Length, Is.GreaterThan(0), "Success flag should exist");
            Assert.That(successFlag[0], Is.EqualTo(0), "Call at maximum depth should fail");

            // Check slot 2 to see at what depth the call failed
            byte[] depthValue = TestState.Get(new StorageCell(Recipient, 2)).ToArray();
            UInt256 depth = 0;
            if (depthValue.Length > 0)
            {
                depth = new UInt256(depthValue, true);
            }
            Assert.That(depth, Is.EqualTo((UInt256)1024), "Call should fail at depth 1024");
        }

        [Test]
        public void Create_fails_at_max_depth()
        {
            // Test contract: counter in slot 0, create result in slot 1, depth of failure in slot 2
            byte[] contractCode = new byte[]
            {
                // Initialize counter to 0
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Loop start
                (byte)Instruction.JUMPDEST,

                // Load and increment counter
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.ADD,
                (byte)Instruction.DUP1,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Create new contract
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.CREATE,

                // Store CREATE result in slot 1
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SSTORE,

                // Store current depth in slot 2
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x02,
                (byte)Instruction.SSTORE,

                // Check if CREATE returned 0
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.EQ,
                (byte)Instruction.PUSH1, 0x3B, // Jump to the END
                (byte)Instruction.JUMPI,

                // Call the newly created contract
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SLOAD,  // Address from slot 1
                (byte)Instruction.PUSH2, 0x03, 0xE8, // 1000 gas
                (byte)Instruction.CALL,
                (byte)Instruction.POP,    // Ignore result

                // Decrement counter
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SWAP1,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // END
                (byte)Instruction.JUMPDEST,
                (byte)Instruction.STOP
            };

            // Make sure the account exists before inserting code
            if (!TestState.AccountExists(Recipient))
            {
                TestState.CreateAccount(Recipient, 100.Ether());
            }

            TestState.InsertCode(Recipient, Keccak.Compute(contractCode), contractCode, Spec);
            TestState.Commit(Spec);

            // Call the contract with enough gas
            // EIP-2565 requires more gas as the ModExp operation is more expensive
            long gasLimit = Spec.IsEip2565Enabled ? 50000000 : 10000000;
            byte[] callCode = Prepare.EvmCode
                .Call(Recipient, gasLimit)
                .Done;

            TestAllTracerWithOutput receipt = Execute(gasLimit + 1000000, callCode);
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "Transaction should succeed");

            // Check storage slot 1 to see if CREATE eventually failed
            byte[] createResult = TestState.Get(new StorageCell(Recipient, 1)).ToArray();
            Assert.That(createResult.Length, Is.GreaterThan(0), "CREATE result should exist");
            Assert.That(createResult[0], Is.EqualTo(0), "CREATE at maximum depth should fail");

            // Check slot 2 to see at what depth the CREATE failed
            byte[] depthValue = TestState.Get(new StorageCell(Recipient, 2)).ToArray();
            UInt256 depth = 0;
            if (depthValue.Length > 0)
            {
                depth = new UInt256(depthValue, true);
            }
            Assert.That(depth, Is.EqualTo((UInt256)1024), "CREATE should fail at depth 1024");
        }

        [Test]
        public void Delegatecall_fails_at_max_depth()
        {
            // Test contract: counter in slot 0, delegatecall result in slot 1, depth of failure in slot 2
            byte[] contractCode = new byte[]
            {
                // Initialize counter to 0
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Loop start
                (byte)Instruction.JUMPDEST,

                // Load and increment counter
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.ADD,
                (byte)Instruction.DUP1,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // Delegatecall to self
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.ADDRESS,
                (byte)Instruction.PUSH3, 0x01, 0x86, 0xA0, // 100,000 gas
                (byte)Instruction.DELEGATECALL,

                // Store call result in slot 1
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SSTORE,

                // Store current depth in slot 2
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x02,
                (byte)Instruction.SSTORE,

                // Check if call failed
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.EQ,
                (byte)Instruction.PUSH1, 0x3C,
                (byte)Instruction.JUMPI,

                // Decrement counter for successful call
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SLOAD,
                (byte)Instruction.PUSH1, 0x01,
                (byte)Instruction.SWAP1,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1, 0x00,
                (byte)Instruction.SSTORE,

                // END
                (byte)Instruction.JUMPDEST,
                (byte)Instruction.STOP
            };

            // Make sure the account exists before inserting code
            if (!TestState.AccountExists(Recipient))
            {
                TestState.CreateAccount(Recipient, 100.Ether());
            }

            TestState.InsertCode(Recipient, Keccak.Compute(contractCode), contractCode, Spec);
            TestState.Commit(Spec);

            // Call the contract with enough gas
            // EIP-2565 requires more gas as the ModExp operation is more expensive
            long gasLimit = Spec.IsEip2565Enabled ? 50000000 : 10000000;
            byte[] callCode = Prepare.EvmCode
                .Call(Recipient, gasLimit)
                .Done;

            TestAllTracerWithOutput receipt = Execute(gasLimit + 1000000, callCode);
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "Transaction should succeed");

            // Check storage slot 1 to see if a delegatecall failed
            byte[] successFlag = TestState.Get(new StorageCell(Recipient, 1)).ToArray();
            Assert.That(successFlag.Length, Is.GreaterThan(0), "Success flag should exist");
            Assert.That(successFlag[0], Is.EqualTo(0), "DELEGATECALL at maximum depth should fail");

            // Check slot 2 to see at what depth the delegatecall failed
            byte[] depthValue = TestState.Get(new StorageCell(Recipient, 2)).ToArray();
            UInt256 depth = 0;
            if (depthValue.Length > 0)
            {
                depth = new UInt256(depthValue, true);
            }
            Assert.That(depth, Is.EqualTo((UInt256)1024), "DELEGATECALL should fail at depth 1024");
        }
    }
}
