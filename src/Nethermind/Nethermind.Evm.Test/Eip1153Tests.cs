// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using System.Diagnostics;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// Tests functionality of Transient Storage
    /// </summary>
    internal class Eip1153Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

        /// <summary>
        /// Transient storage should be activated after activation hardfork
        /// </summary>
        [Test]
        public void after_activation_can_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(1)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
        }

        /// <summary>
        /// Transient storage should not be activated until after activation hardfork
        /// </summary>
        [Test]
        public void before_activation_can_not_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.GrayGlacierBlockNumber, 100000, code, timestamp: MainnetSpecProvider.CancunBlockTimestamp - 1);
            Assert.AreEqual(StatusCode.Failure, result.StatusCode);

            code = Prepare.EvmCode
                .LoadDataFromTransientStorage(1)
                .Done;

            result = Execute(MainnetSpecProvider.GrayGlacierBlockNumber, 100000, code, timestamp: MainnetSpecProvider.CancunBlockTimestamp - 1);
            Assert.AreEqual(StatusCode.Failure, result.StatusCode);
        }

        /// <summary>
        /// Uninitialized transient storage is zero
        /// </summary>
        [Test]
        public void tload_uninitialized_returns_zero()
        {
            byte[] code = Prepare.EvmCode
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .Return(32, 0)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            // Should be 0 since it's not yet set
            Assert.AreEqual(0, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Simple performance test
        /// </summary>
        [Explicit("Depends on hardware")]
        [Test]
        public void transient_storage_performance_test()
        {
            Stopwatch stopwatch = new Stopwatch();
            long blockGasLimit = 30000000;
            long numOfOps = (long)(blockGasLimit * .95) / (GasCostOf.TLoad + GasCostOf.TStore + GasCostOf.VeryLow * 4);
            Prepare prepare = Prepare.EvmCode;
            for (long i = 0; i < numOfOps; i++)
            {
                prepare.StoreDataInTransientStorage(1, 8);
                prepare.LoadDataFromTransientStorage(1);
                prepare.Op(Instruction.POP);
            }

            byte[] code = prepare.Done;

            stopwatch.Start();
            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.GrayGlacierBlockNumber, blockGasLimit, code, blockGasLimit, Timestamp);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            stopwatch.Stop();
            Assert.IsTrue(stopwatch.ElapsedMilliseconds < 5000);
        }

        /// <summary>
        /// Simple functionality test
        /// </summary>
        [Test]
        public void tload_after_tstore()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .Return(32, 0)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Testing transient data store/load from different locations
        /// </summary>
        /// <param name="loadLocation">Location</param>
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void tload_after_tstore_from_different_locations(int loadLocation)
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(loadLocation)
                .DataOnStackToMemory(0)
                .Return(32, 0)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            Assert.AreEqual(0, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Contracts have separate transient storage
        /// </summary>
        [Test]
        public void tload_from_different_contract()
        {
            // TLOAD and RETURN the resulting value
            byte[] contractCode = Prepare.EvmCode
                .PushData(1)
                .Op(Instruction.TLOAD)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Store 8 at index 1 and call contract from above
            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // If transient state was not isolated, the return value would be 8
            Assert.AreEqual(0, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Reentrant calls access the same transient storage
        /// </summary>
        [Test]
        public void tload_from_reentrant_call()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(78)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                // Return the response
                .ReturnInnerCallResult()

                // Reentrant, TLOAD and return value
                .Op(Instruction.JUMPDEST)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Reentrant calls can manipulate the same transient storage
        /// </summary>
        [Test]
        public void tstore_from_reentrant_call()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(78)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                // Return the response
                .ReturnInnerCallResult()

                // Reentrant, TLOAD and return value
                .Op(Instruction.JUMPDEST) // PC = 78
                .StoreDataInTransientStorage(1, 9)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(9, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Successfully returned calls do not revert transient storage writes
        /// </summary>
        [Test]
        public void tstore_from_reentrant_call_read_by_caller()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(77)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                // TLOAD and return value (should be 9)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TSTORE 9
                .Op(Instruction.JUMPDEST) // PC = 77
                .StoreDataInTransientStorage(1, 9)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(9, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Revert undoes the transient storage write from the failed call
        /// </summary>
        [Test]
        public void revert_resets_transient_state()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(77)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                // TLOAD and return value
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TSTORE 9 but REVERT
                .Op(Instruction.JUMPDEST) // PC = 77
                .StoreDataInTransientStorage(1, 9)
                .Op(Instruction.REVERT)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // Should be original TSTORE value
            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Revert undoes all the transient storage writes to the same key from the failed call
        /// </summary>
        [Test]
        public void revert_resets_all_transient_state()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(77)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                // TLOAD and return value
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TSTORE 9 but REVERT
                .Op(Instruction.JUMPDEST) // PC = 77
                .StoreDataInTransientStorage(1, 9)
                .StoreDataInTransientStorage(1, 10)
                .Op(Instruction.REVERT)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // Should be original TSTORE value
            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Revert undoes transient storage writes from inner calls that successfully returned
        /// </summary>
        [Test]
        public void revert_resets_transient_state_from_succesful_calls()
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check call depth
                .PushData(0)
                .Op(Instruction.CALLDATALOAD)
                // Store input in mem and reload it to stack
                .DataOnStackToMemory(5)
                .PushData(5)
                .Op(Instruction.MLOAD)

                // See if we're at call depth 1
                .PushData(1)
                .Op(Instruction.EQ)
                .PushData(84)
                .Op(Instruction.JUMPI)

                // See if we're at call depth 2
                .PushData(5)
                .Op(Instruction.MLOAD)
                .PushData(2)
                .Op(Instruction.EQ)
                .PushData(135)
                .Op(Instruction.JUMPI)

                // Call depth = 0, call self after TSTORE 8
                .StoreDataInTransientStorage(1, 8)

                    // Recursive call with input
                    // Depth++
                    .PushData(5)
                    .Op(Instruction.MLOAD)
                    .PushData(1)
                    .Op(Instruction.ADD)

                    .CallWithInput(TestItem.AddressD, 50000)

                // TLOAD and return value
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Call depth 1, TSTORE 9 but REVERT after recursion
                .Op(Instruction.JUMPDEST) // PC = 84
                .StoreDataInTransientStorage(1, 9)

                    // Recursive call with input
                    // Depth++
                    .PushData(5)
                    .Op(Instruction.MLOAD)
                    .PushData(1)
                    .Op(Instruction.ADD)

                    .CallWithInput(TestItem.AddressD, 50000)

                .Op(Instruction.REVERT)

                // Call depth 2, TSTORE 10 and complete
                .Op(Instruction.JUMPDEST) // PC = 135
                .StoreDataInTransientStorage(1, 10)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .CallWithInput(TestItem.AddressD, 50000, new byte[32])
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // Should be original TSTORE value
            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Transient storage cannot be manipulated in a static context
        /// </summary>
        [TestCase(Instruction.CALL, 1)]
        [TestCase(Instruction.STATICCALL, 0)]
        public void tstore_in_staticcall(Instruction callType, int expectedResult)
        {
            byte[] contractCode = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .PushData(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract (1 if successful)
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 7)
                .DynamicCallWithInput(callType, TestItem.AddressD, 50000, new byte[32])
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Transient storage cannot be manipulated in a static context when calling self
        /// </summary>
        [TestCase(Instruction.CALL, 9)]
        [TestCase(Instruction.STATICCALL, 8)]
        public void tstore_from_static_reentrant_call(Instruction callType, int expectedResult)
        {
            // If caller is self, TSTORE 9 and break recursion
            // Else, TSTORE 8 and call self, return the result of TLOAD
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(113)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE 8
                .StoreDataInTransientStorage(1, 8)
                .DynamicCallWithInput(callType, TestItem.AddressD, 50000, new byte[32])
                // Return the TLOAD value
                // Should be 8 if call fails, 9 if success
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TSTORE 9
                .Op(Instruction.JUMPDEST) // PC = 113
                .StoreDataInTransientStorage(1, 9)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Transient storage cannot be manipulated in a nested static context
        /// </summary>
        [TestCase(Instruction.CALL, 10)]
        [TestCase(Instruction.STATICCALL, 8)]
        public void tstore_from_nonstatic_reentrant_call_with_static_intermediary(Instruction callType, int expectedResult)
        {
            // If caller is self, TLOAD and return value (break recursion)
            // Else, TSTORE and call self, return the response
            byte[] contractCode = Prepare.EvmCode
                // Check call depth
                .PushData(0)
                .Op(Instruction.CALLDATALOAD)
                // Store input in mem and reload it to stack
                .DataOnStackToMemory(5)
                .PushData(5)
                .Op(Instruction.MLOAD)

                // See if we're at call depth 1
                .PushData(1)
                .Op(Instruction.EQ)
                .PushData(84)
                .Op(Instruction.JUMPI)

                // See if we're at call depth 2
                .PushData(5)
                .Op(Instruction.MLOAD)
                .PushData(2)
                .Op(Instruction.EQ)
                .PushData(140)
                .Op(Instruction.JUMPI)

                // Call depth = 0, call self after TSTORE 8
                .StoreDataInTransientStorage(1, 8)

                    // Recursive call with input
                    // Depth++
                    .PushData(5)
                    .Op(Instruction.MLOAD)
                    .PushData(1)
                    .Op(Instruction.ADD)

                    .DynamicCallWithInput(callType, TestItem.AddressD, 50000)

                // TLOAD and return value
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Call depth 1, TSTORE 9 but REVERT after recursion
                .Op(Instruction.JUMPDEST) // PC = 84

                    // Recursive call with input
                    // Depth++
                    .PushData(5)
                    .Op(Instruction.MLOAD)
                    .PushData(1)
                    .Op(Instruction.ADD)
                    .CallWithInput(TestItem.AddressD, 50000)

                    // TLOAD and return value
                    .LoadDataFromTransientStorage(1)
                    .DataOnStackToMemory(0)
                    .PushData(32)
                    .PushData(0)
                    .Op(Instruction.RETURN)

                // Call depth 2, TSTORE 10 and complete
                .Op(Instruction.JUMPDEST) // PC = 140
                .StoreDataInTransientStorage(1, 10) // This will fail
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .CallWithInput(TestItem.AddressD, 50000, new byte[32])
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            // Should be original TSTORE value
            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Delegatecall manipulates transient storage in the context of the current address
        /// </summary>
        [TestCase(Instruction.CALL, 7)]
        [TestCase(Instruction.DELEGATECALL, 8)]
        public void tstore_in_delegatecall(Instruction callType, int expectedResult)
        {
            byte[] contractCode = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 7)
                .DynamicCallWithInput(callType, TestItem.AddressD, 50000, new byte[32])
                // TLOAD and return value
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Delegatecall reads transient storage in the context of the current address
        /// </summary>
        [TestCase(Instruction.CALL, 0)]
        [TestCase(Instruction.DELEGATECALL, 7)]
        public void tload_in_delegatecall(Instruction callType, int expectedResult)
        {
            byte[] contractCode = Prepare.EvmCode
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 7)
                .DynamicCallWithInput(callType, TestItem.AddressD, 50000, new byte[32])
                // Return response from nested call
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }

        /// <summary>
        /// Zeroing out a transient storage slot does not result in gas refund
        /// </summary>
        [Test]
        public void tstore_does_not_result_in_gasrefund()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 7)
                .StoreDataInTransientStorage(1, 0)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.TStore * 2, receipt.GasSpent, "gas");
        }

        /// <summary>
        /// Transient storage does not persist beyond a single transaction
        /// </summary>
        [Test]
        public void transient_state_not_persisted_across_txs()
        {
            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .LoadDataFromTransientStorage(1)
                // See if we're at call depth 1
                .PushData(1)
                .Op(Instruction.EQ)
                .PushData(24)
                .Op(Instruction.JUMPI)

                // TSTORE 1 and Return 1
                .StoreDataInTransientStorage(1, 1)
                .PushData(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Return 0
                .Op(Instruction.JUMPDEST) // PC = 24
                .PushData(0)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(1, (int)result.ReturnValue.ToUInt256());

            // If transient state persisted across txs, calling again would return 0
            result = Execute(code);
            Assert.AreEqual(1, (int)result.ReturnValue.ToUInt256());
        }


        /// <summary>
        /// Transient storage can be accessed in a static context when calling self
        /// </summary>
        [TestCase(Instruction.CALL, 8)]
        [TestCase(Instruction.STATICCALL, 8)]
        public void tload_from_static_reentrant_call(Instruction callType, int expectedResult)
        {
            // If caller is self, TLOAD and break recursion
            // Else, TSTORE 8 and call self, return the result of the inner call
            byte[] contractCode = Prepare.EvmCode
                // Check if caller is self
                .Op(Instruction.CALLER)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EQ)
                .PushData(114)
                .Op(Instruction.JUMPI)

                // Non-reentrant, call self after TSTORE 8
                .StoreDataInTransientStorage(1, 8)
                .DynamicCallWithInput(callType, TestItem.AddressD, 50000, new byte[32])
                .ReturnInnerCallResult()

                // Reentrant, TLOAD and return
                .Op(Instruction.JUMPDEST) // PC = 114
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            WorldState.CreateAccount(TestItem.AddressD, 1.Ether());
            WorldState.InsertCode(TestItem.AddressD, contractCode, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .ReturnInnerCallResult()
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            Assert.AreEqual(expectedResult, (int)result.ReturnValue.ToUInt256());
        }
    }
}
