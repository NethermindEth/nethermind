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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    internal class Eip1153Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [Test]
        public void after_shanghai_can_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(1)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
        }

        [Test]
        public void before_shanghai_can_not_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(1)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber - 1, 100000, code);
            Assert.AreEqual(StatusCode.Failure, result.StatusCode);
        }

        [Test]
        public void tload_uninitialized_returns_zero()
        {
            byte[] code = Prepare.EvmCode
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .Return(32, 0)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            // Should be 0 since it's not yet set
            Assert.AreEqual(0, (int)result.ReturnValue.ToUInt256());
        }

        [Test]
        public void tload_after_tstore()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .Return(32, 0)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }

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

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
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

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Store 8 at index 1 and call contract from above
            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .StoreDataInTransientStorage(1, 8)
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TLOAD and return value
                .Op(Instruction.JUMPDEST)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)

                // Reentrant, TLOAD and return value
                .Op(Instruction.JUMPDEST) // PC = 78
                .StoreDataInTransientStorage(1, 9)
                .LoadDataFromTransientStorage(1)
                .DataOnStackToMemory(0)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

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

                    .DataOnStackToMemory(0)
                    .PushData(0)
                    .PushData(0)
                    .PushData(32)
                    .PushData(0)
                    .PushData(0)
                    .PushData(TestItem.AddressD)
                    .PushData(50000)
                    .Op(Instruction.CALL)

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

                    .DataOnStackToMemory(0)
                    .PushData(0)
                    .PushData(0)
                    .PushData(32)
                    .PushData(0)
                    .PushData(0)
                    .PushData(TestItem.AddressD)
                    .PushData(50000)
                    .Op(Instruction.CALL)

                .Op(Instruction.REVERT)

                // Call depth 2, TSTORE 10 and complete
                .Op(Instruction.JUMPDEST) // PC = 135
                .StoreDataInTransientStorage(1, 10)
                .Done;

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            Keccak selfDestructCodeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(TestItem.AddressD, selfDestructCodeHash, Spec);

            // Return the result received from the contract
            byte[] code = Prepare.EvmCode
                .CallWithInput(TestItem.AddressD, 50000, new byte[32])
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);

            // Should be original TSTORE value
            Assert.AreEqual(8, (int)result.ReturnValue.ToUInt256());
        }
    }
}
