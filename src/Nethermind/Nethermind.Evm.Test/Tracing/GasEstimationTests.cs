// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [Parallelizable(ParallelScope.All)]
    public class GasEstimationTests
    {
        [Test]
        public void Estimate_UseErrorMarginOutsideBounds_ReturnsError([Values(ulong.MaxValue, 10000UL, 10001UL)] ulong errorMargin)
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(tx).TestObject;

            testEnvironment.Estimate(tx, block.Header, out string? err, errorMargin);

            Assert.That(err, Is.Not.Null, "an error margin of 100% or more is refused");
        }

        [Test]
        public void Should_return_zero_with_insufficient_balance_error_when_sender_is_address_zero_with_value_transfer()
        {
            using TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000ul)
                .WithSenderAddress(Address.Zero)
                .WithValue(1.Ether)
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            ulong estimate = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(estimate, Is.EqualTo(0ul), "Should return 0 when Address.Zero has insufficient balance for value transfer");
            Assert.That(err, Is.EqualTo(GasEstimator.InsufficientBalance), "Should provide insufficient balance error message");
        }

        [Test]
        public void Should_succeed_when_address_zero_has_no_value_transfer()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(100000ul)
                .WithGasPrice(0ul)
                .WithSenderAddress(Address.Zero)
                .WithValue(0) // No value transfer - should work even with zero balance
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            ulong estimate = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(estimate, Is.GreaterThan(0ul), "Should succeed when Address.Zero has no value transfer");
            Assert.That(err, Is.Null, "No error should occur for Address.Zero with no value transfer");
        }

        [TestCase(50_000ul, false)]
        [TestCase(500_000ul, false)]
        [TestCase(1_000_000ul, false)]
        [TestCase(1_100_000ul, true)]
        public void Should_estimate_gas_for_explicit_gas_check_and_revert(ulong gasLimit, bool shouldSucceed)
        {
            TestEnvironment testEnvironment = new();
            Address contractAddress = TestItem.AddressB;
            int check = 1_000_000;
            byte[] contractCode = Bytes.FromHexString($"0x62{check:x6}5a10600f576001600055005b6000806000fd");
            testEnvironment.InsertContract(contractAddress, contractCode);

            Transaction tx = Build.A.Transaction
                .WithData([0x00, 0x00, 0x00, 0x00])
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1) // Ensure opcode `REVERT` is available
                .WithTransactions(tx).TestObject;
            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            if (shouldSucceed)
            {
                Assert.That(result, Is.GreaterThan(1_000_000ul), "Gas estimation should account for the gas threshold in the contract");
                Assert.That(err, Is.Null);
            }
            else
            {
                Assert.That(err, Is.Not.Null, "Gas estimation should fail when the gas limit is too low");
            }
        }

        [Test]
        public void Should_estimate_gas_when_inner_call_reverts_but_transaction_succeeds()
        {
            // Reproduces https://github.com/NethermindEth/nethermind/issues/10552
            // GnosisSafe createProxyWithNonce has inner calls that revert (try/catch pattern).
            // The bug: ReportOperationError sets OutOfGas=true for ANY revert, even inner ones,
            // causing the binary search in gas estimation to always think the tx failed.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            Address callerAddress = TestItem.AddressC;

            // Reverter contract: always reverts with empty data
            byte[] reverterCode = Prepare.EvmCode
                .PushData(0x00)
                .PushData(0x00)
                .Op(Instruction.REVERT)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            // Caller contract: CALLs reverter (which reverts), catches the revert, then succeeds.
            // This simulates GnosisSafe's try/catch pattern.
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 100_000)  // inner call that reverts - return value 0 on stack
                .Op(Instruction.POP)             // discard call result
                .PushData(0x01)                  // value = 1
                .PushData(0x00)                  // key = 0
                .Op(Instruction.SSTORE)          // store 1 at slot 0 (proves execution continued)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            ulong gasLimit = 300_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed when inner call reverts but transaction succeeds overall");
            Assert.That(err, Is.Null, "No error should occur - inner reverts should not be treated as top-level failures");
        }

        [Test]
        public void Should_estimate_gas_for_create2_with_setup_call_pattern()
        {
            // Simulates GnosisSafe createProxyWithNonce: CREATE2 deploys a proxy, then
            // the caller does a CALL to the newly deployed proxy for setup.
            // The setup call may revert internally but the overall tx succeeds.
            using TestEnvironment testEnvironment = new();

            // The "proxy" runtime code: just stores a value (simulating successful setup)
            byte[] proxyRuntimeCode = Prepare.EvmCode
                .PushData(0x42)    // value
                .PushData(0x00)    // key
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            // Init code that returns the runtime code
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(proxyRuntimeCode)
                .Done;

            // Factory contract: CREATE2 the proxy, then CALL setup on it
            // CREATE2(value=0, offset=0, size=initCode.length, salt=0)
            // CALL(gas, addr_from_create2, value=0, ...)
            byte[] factoryCode = Prepare.EvmCode
                .Create2(initCode, new byte[] { 0x01 }, 0) // CREATE2 with salt=1
                .Op(Instruction.DUP1)       // duplicate address for CALL
                .PushData(0x00)             // retSize
                .PushData(0x00)             // retOffset
                .PushData(0x00)             // argSize
                .PushData(0x00)             // argOffset
                .PushData(0x00)             // value
                .Op(Instruction.SWAP5)      // bring address to top (after value)
                .PushData(50_000)           // gas for setup call
                .Op(Instruction.CALL)
                .Op(Instruction.POP)        // discard call result
                .Op(Instruction.POP)        // discard remaining address copy
                .Op(Instruction.STOP)
                .Done;

            Address factoryAddress = TestItem.AddressB;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ConstantinopleFixBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed for CREATE2 + setup call pattern");
            Assert.That(err, Is.Null, "No error for CREATE2 + setup call");
        }

        [Test]
        public void Should_estimate_gas_with_multiple_inner_calls_mixed_reverts()
        {
            // Contract makes 3 inner calls: first reverts, second succeeds, third reverts.
            // Transaction should still succeed and gas estimation should work.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            Address succeederAddress = TestItem.AddressC;

            // Contract that always reverts
            byte[] reverterCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            // Contract that succeeds (stores value)
            byte[] succeederCode = Prepare.EvmCode
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(succeederAddress, succeederCode);

            // Caller: calls reverter, succeeder, reverter - catches all failures
            Address callerAddress = TestItem.AddressD;
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 30_000)   // call 1: reverts
                .Op(Instruction.POP)
                .Call(succeederAddress, 50_000)  // call 2: succeeds
                .Op(Instruction.POP)
                .Call(reverterAddress, 30_000)   // call 3: reverts
                .Op(Instruction.POP)
                .PushData(0xFF)
                .PushData(0x01)
                .Op(Instruction.SSTORE)          // store to prove we got here
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed with mixed inner reverts");
            Assert.That(err, Is.Null, "No error when inner calls revert but overall tx succeeds");
        }

        [Test]
        public void Should_estimate_gas_when_inner_call_runs_out_of_gas_but_caller_handles_it()
        {
            // Verifies that inner OOG (caught by the caller) does not fail gas estimation.
            // OutOfGas must be nesting-aware (only set at top level), matching Geth behavior.
            // Geth's binary search checks result.Failed() which only reflects the top-level outcome.
            // See: https://github.com/ethereum/go-ethereum/blob/master/eth/gasestimator/gasestimator.go
            using TestEnvironment testEnvironment = new();

            // Contract that consumes all gas via infinite loop (will always OOG)
            Address gasGuzzlerAddress = TestItem.AddressB;
            byte[] gasGuzzlerCode = Prepare.EvmCode
                .Op(Instruction.JUMPDEST)   // offset 0
                .PushData((byte)0x00)
                .Op(Instruction.JUMP)       // jump back to 0
                .Done;
            testEnvironment.InsertContract(gasGuzzlerAddress, gasGuzzlerCode);

            // Middle contract: calls gas guzzler with limited gas, catches OOG
            Address middleAddress = TestItem.AddressC;
            byte[] middleCode = Prepare.EvmCode
                .Call(gasGuzzlerAddress, 1_000)  // only 1000 gas - will OOG
                .Op(Instruction.POP)             // discard result (0 = failure)
                .PushData(0x01)
                .PushData((byte)0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(middleAddress, middleCode);

            // Outer caller
            Address callerAddress = TestItem.AddressD;
            byte[] callerCode = Prepare.EvmCode
                .Call(middleAddress, 100_000)
                .Op(Instruction.POP)
                .PushData(0x02)
                .PushData(0x01)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(callerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed when inner call OOGs but caller handles it");
            Assert.That(err, Is.Null, "No error - inner OOG should not affect top-level estimation");
        }

        [TestCase(50_000ul, true)]
        [TestCase(500_000ul, true)]
        [TestCase(1_000ul, false)]
        public void Should_estimate_gas_with_gas_sensitive_branching(ulong gasThreshold, bool shouldSucceed)
        {
            // Contract that checks gasLeft() and branches: if gasLeft >= threshold, SSTORE; else REVERT.
            // Tests that the binary search correctly handles gas-dependent execution paths.
            using TestEnvironment testEnvironment = new();

            Address contractAddress = TestItem.AddressB;

            // Use the existing pattern from Should_estimate_gas_for_explicit_gas_check_and_revert
            // Bytecode: PUSH3 <threshold>, GAS, LT, PUSH1 <revert_pc>, JUMPI, PUSH1 1, PUSH1 0, SSTORE, STOP, JUMPDEST, PUSH1 0, PUSH1 0, REVERT
            ulong check = gasThreshold;
            byte[] contractCode = Bytes.FromHexString($"0x62{check:x6}5a10600f576001600055005b6000806000fd");
            testEnvironment.InsertContract(contractAddress, contractCode);

            ulong gasLimit = 1_100_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            if (shouldSucceed)
            {
                Assert.That(result, Is.GreaterThan(0), "Gas estimation should find enough gas for the success path");
                Assert.That(err, Is.Null, "No error - binary search should find gas level above threshold");
            }
            else
            {
                Assert.That(result, Is.GreaterThan(0), "Low threshold should always succeed");
                Assert.That(err, Is.Null);
            }
        }

        [Test]
        public void Should_estimate_gas_for_create_with_constructor_making_calls()
        {
            // CREATE deploys a contract whose constructor makes an external CALL.
            // The constructor call might revert but CREATE still succeeds.
            using TestEnvironment testEnvironment = new();

            // External contract that reverts
            Address externalAddress = TestItem.AddressB;
            byte[] externalCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(externalAddress, externalCode);

            // Runtime code (deployed contract's code)
            byte[] runtimeCode = Prepare.EvmCode
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            // Init code: calls external (which reverts, but init code catches it), then returns runtime code
            byte[] initCode = Prepare.EvmCode
                .Call(externalAddress, 10_000)
                .Op(Instruction.POP)             // discard call result
                .ForInitOf(runtimeCode)
                .Done;

            // Factory: CREATE with init code, then STOP
            Address factoryAddress = TestItem.AddressC;
            byte[] factoryCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.POP)             // discard created address
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed for CREATE with constructor that makes calls");
            Assert.That(err, Is.Null, "No error for constructor-call pattern");
        }

        [Test]
        public void Should_estimate_gas_consistently_across_repeated_calls()
        {
            // Tests that repeated gas estimation on the same contract yields consistent results.
            // Each call uses a fresh environment; this guards against non-deterministic estimation behavior across runs.
            using TestEnvironment testEnvironment = new();

            Address reverterAddress = TestItem.AddressB;
            byte[] reverterCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(reverterAddress, reverterCode);

            Address callerAddress = TestItem.AddressC;
            byte[] callerCode = Prepare.EvmCode
                .Call(reverterAddress, 30_000)
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(callerAddress, callerCode);

            ulong gasLimit = 300_000ul;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong? firstResult = null;
            for (int i = 0; i < 10; i++)
            {
                TestEnvironment freshEnv = new();
                freshEnv.InsertContract(reverterAddress, reverterCode);
                freshEnv.InsertContract(callerAddress, callerCode);

                Transaction tx = Build.A.Transaction
                    .WithGasLimit(gasLimit)
                    .WithTo(callerAddress)
                    .WithSenderAddress(TestItem.AddressA)
                    .TestObject;

                ulong result = freshEnv.Estimate(tx, block.Header, out string? err);

                Assert.That(result, Is.GreaterThan(0ul), $"Iteration {i}: gas estimation should succeed");
                Assert.That(err, Is.Null, $"Iteration {i}: no error expected");

                firstResult ??= result;
                Assert.That(result, Is.EqualTo(firstResult.Value), $"Iteration {i}: result should be consistent");

                freshEnv.Dispose();
            }
        }

        [Test]
        public void Should_estimate_gas_for_deeply_nested_calls()
        {
            // Chain of 4 nested CALLs.
            // A -> B -> C -> D (all succeed)
            using TestEnvironment testEnvironment = new();

            // Contract D: leaf, just stores and stops
            Address addrD = TestItem.AddressD;
            byte[] codeD = Prepare.EvmCode
                .PushData(0x04)
                .PushData(0x04)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrD, codeD);

            // Contract C: calls D
            Address addrC = TestItem.AddressC;
            byte[] codeC = Prepare.EvmCode
                .Call(addrD, 50_000)
                .Op(Instruction.POP)
                .PushData(0x03)
                .PushData(0x03)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrC, codeC);

            // Contract B: calls C
            Address addrB = TestItem.AddressB;
            byte[] codeB = Prepare.EvmCode
                .Call(addrC, 100_000)
                .Op(Instruction.POP)
                .PushData(0x02)
                .PushData(0x02)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrB, codeB);

            // Contract A: calls B (this is what the tx calls)
            Address addrA = new("0x0000000000000000000000000000000000000042");
            byte[] codeA = Prepare.EvmCode
                .Call(addrB, 200_000)
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x01)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(addrA, codeA);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(addrA)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed for deeply nested call chain");
            Assert.That(err, Is.Null, "No error for deeply nested calls");
        }

        [Test]
        public void Should_estimate_gas_for_nested_create2_with_inner_revert_in_constructor()
        {
            // CREATE2 deploys a contract whose constructor calls an external contract that reverts.
            // Constructor catches the revert and continues. This is the GnosisSafe pattern:
            // createProxyWithNonce -> CREATE2 -> proxy constructor -> setup() call -> possible revert
            using TestEnvironment testEnvironment = new();

            // External contract that always reverts with data
            Address externalAddress = TestItem.AddressB;
            byte[] externalCode = Prepare.EvmCode
                .StoreDataInMemory(0, new byte[] { 0xDE, 0xAD })
                .Revert(2, 0)
                .Done;
            testEnvironment.InsertContract(externalAddress, externalCode);

            // Runtime code (what the proxy becomes after deployment)
            byte[] runtimeCode = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            // Init code: calls external (reverts, caught), then returns runtime code
            byte[] initCode = Prepare.EvmCode
                .Call(externalAddress, 20_000)   // will revert, returns 0
                .Op(Instruction.POP)              // discard failure result
                .ForInitOf(runtimeCode)
                .Done;

            // Factory: CREATE2 with salt, verify address is non-zero, STOP
            Address factoryAddress = TestItem.AddressC;
            byte[] factoryCode = Prepare.EvmCode
                .Create2(initCode, new byte[] { 0xAB, 0xCD }, 0) // CREATE2 with salt
                .Op(Instruction.POP)
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)           // record success
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(factoryAddress, factoryCode);

            ulong gasLimit = 500_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(factoryAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ConstantinopleFixBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed for CREATE2 with inner revert in constructor");
            Assert.That(err, Is.Null, "No error for GnosisSafe-like CREATE2 pattern");
        }

        [Test]
        public void Should_return_revert_error_when_top_level_call_reverts_with_data()
        {
            // Ensures gas estimation properly reports revert data when the top-level call reverts.
            using TestEnvironment testEnvironment = new();

            Address contractAddress = TestItem.AddressB;
            // Store revert reason in memory, then REVERT with it
            byte[] contractCode = Prepare.EvmCode
                .StoreDataInMemory(0, new byte[] { 0x08, 0xC3, 0x79, 0xA0 }) // Error(string) selector
                .Revert(4, 0)
                .Done;
            testEnvironment.InsertContract(contractAddress, contractCode);

            ulong gasLimit = 300_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(contractAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.EqualTo(0ul), "Gas estimation should fail when top-level call reverts");
            Assert.That(err, Is.Not.Null, "Should report an error when top-level reverts");
        }

        [Test]
        public void Should_estimate_gas_with_delegatecall_that_reverts_internally()
        {
            // DELEGATECALL that reverts internally - the revert happens in the caller's context
            // but at a nested level. Gas estimation should still succeed.
            using TestEnvironment testEnvironment = new();

            // Implementation that reverts
            Address implAddress = TestItem.AddressB;
            byte[] implCode = Prepare.EvmCode
                .Revert(0, 0)
                .Done;
            testEnvironment.InsertContract(implAddress, implCode);

            // Proxy: DELEGATECALL to impl (reverts), catches it, then succeeds
            Address proxyAddress = TestItem.AddressC;
            byte[] proxyCode = Prepare.EvmCode
                .DelegateCall(implAddress, 30_000)
                .Op(Instruction.POP)             // discard result
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(proxyAddress, proxyCode);

            ulong gasLimit = 300_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(proxyAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(result, Is.GreaterThan(0ul), "Gas estimation should succeed when DELEGATECALL reverts but caller handles it");
            Assert.That(err, Is.Null, "No error for caught DELEGATECALL revert");
        }

        [Test]
        public void Estimate_subcall_contract_uses_optimistic_multiplier_not_margin_multiplier()
        {
            using TestEnvironment testEnvironment = new();

            Address innerAddress = TestItem.AddressB;
            byte[] innerCode = Prepare.EvmCode
                .PushData(0x01)
                .PushData(0x00)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(innerAddress, innerCode);

            Address outerAddress = TestItem.AddressC;
            byte[] outerCode = Prepare.EvmCode
                .Call(innerAddress, 100_000)
                .Op(Instruction.POP)
                .PushData(0x02)
                .PushData(0x01)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;
            testEnvironment.InsertContract(outerAddress, outerCode);

            ulong gasLimit = 300_000;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(outerAddress)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber + 1)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err, errorMargin: 0);

            Assert.That(err, Is.Null);
            Assert.That(result, Is.GreaterThan(Transaction.BaseTxGasCost));
        }

        [Test]
        public void IsSimpleTransfer_returns_false_for_SetCodeTx_with_authorization_list()
        {
            using TestEnvironment testEnvironment = new();

            Address target = TestItem.AddressB;

            ulong gasLimit = 100_000;
            Transaction tx = Build.A.Transaction
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(1)
                .WithGasLimit(gasLimit)
                .WithTo(target)
                .WithSenderAddress(TestItem.AddressA)
                .WithAuthorizationCode(new AuthorizationTuple(1, Address.Zero, 0, new Signature(new byte[64], 0)))
                .TestObject;

            Block block = Build.A.Block
                .WithNumber(MainnetSpecProvider.PragueActivation.BlockNumber)
                .WithTimestamp(MainnetSpecProvider.PragueActivation.Timestamp!.Value)
                .WithTransactions(tx)
                .WithGasLimit(gasLimit)
                .TestObject;

            ulong result = testEnvironment.Estimate(tx, block.Header, out string? err);

            Assert.That(err, Is.Null, err);
            Assert.That(result, Is.GreaterThan(Transaction.BaseTxGasCost));
        }

        private class TestEnvironment : IDisposable
        {
            public ISpecProvider _specProvider;
            public IEthereumEcdsa _ethereumEcdsa;
            public EthereumTransactionProcessor _transactionProcessor;
            public IWorldState _stateProvider;
            public GasEstimator estimator;
            private readonly IDisposable _closer;

            public TestEnvironment()
            {
                _specProvider = MainnetSpecProvider.Instance;
                _stateProvider = TestWorldStateFactory.CreateForTest();
                _closer = _stateProvider.BeginScope(IWorldState.PreGenesis);
                _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);

                EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
                EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
                _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
                _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);

                estimator = new(_transactionProcessor, _stateProvider);
            }

            public ulong Estimate(Transaction tx, BlockHeader header, out string? err, ulong errorMargin = GasEstimator.DefaultErrorMargin)
            {
                GasEstimation estimation = estimator.Estimate(tx, new BlockExecutionContext(header, _specProvider.GetSpec(header)), errorMargin);
                err = estimation.Error;
                return estimation.Gas;
            }

            public void InsertContract(Address contractAddress, byte[] code)
            {
                _stateProvider.CreateAccount(contractAddress, 0);
                _stateProvider.InsertCode(contractAddress, ValueKeccak.Compute(code), code, _specProvider.GenesisSpec);
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);
            }

            public void Dispose() => _closer.Dispose();
        }

        [Test]
        public void Estimates_self_recursive_call_until_exhaustion_without_throwing()
        {
            // PUSH0 x5, ADDRESS, GAS, CALL: recurse into self with all remaining gas until 63/64 or depth stops it.
            using TestEnvironment testEnvironment = new();
            Address recursive = TestItem.AddressB;
            testEnvironment.InsertContract(recursive, Bytes.FromHexString("0x5f5f5f5f5f305af1"));

            ulong gasLimit = 30_000_000ul;
            Transaction tx = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithTo(recursive)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            Block block = Build.A.Block
                .WithNumber(20_000_000)
                .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
                .WithGasLimit(gasLimit)
                .WithTransactions(tx)
                .TestObject;

            ulong result = 0;
            string? err = null;
            Assert.DoesNotThrow(() => result = testEnvironment.Estimate(tx, block.Header, out err));
            Assert.That(err, Is.Null, err);
            Assert.That(result, Is.GreaterThan(GasCostOf.Transaction));
        }
    }
}
