//  Copyright (c) 2018 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip2935Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.BerlinBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        private IReleaseSpec _spec = new ReleaseSpec();
        private ISpecProvider _specProvider;

        [SetUp]
        public override void Setup()
        {
            _specProvider = new SingleReleaseSpecProvider(_spec, 0);
            base.Setup();

            TestState.CreateAccount(IVirtualMachine.BlockhashStorage, 0);
            Keccak codeHash = TestState.UpdateCode(new byte[1] {0});
            TestState.UpdateCodeHash(IVirtualMachine.BlockhashStorage, codeHash, Spec);
            for (int blockNumber = 0; blockNumber < 1024; blockNumber++)
            {
                StorageCell storageCell = new StorageCell(IVirtualMachine.BlockhashStorage, (UInt256) blockNumber);
                UInt256 number256 = (UInt256) blockNumber;

                // set some test blockhashes for the first 1024 blocks

                Storage.Set(storageCell, number256.ToBigEndian());
            }

            Storage.Commit();
            Storage.CommitTrees();
            TestState.Commit(Spec);
            TestState.CommitTree();
            TestState.GetStorageRoot(IVirtualMachine.BlockhashStorage);
        }

        [TestCase(0, 0, 0, false)]
        [TestCase(0, 256, 0, false)]
        [TestCase(0, 257, 0, true)]
        [TestCase(256, 0, 1, false)]
        [TestCase(256, 0, 256, false)]
        [TestCase(256, 0, 257, false)]
        [TestCase(256, 255, 1, false)]
        [TestCase(256, 256, 1, false)]
        [TestCase(256, 512, 1, false)]
        [TestCase(256, 513, 1, true)]
        [TestCase(256, 513, 255, true)]
        [TestCase(256, 513, 256, true)]
        public void Correct_blockhash_strategy_is_used(long transitionBlock, long currentNumber, long requestedNumber, bool useEip2935)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(requestedNumber)
                .Op(Instruction.BLOCKHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Spec.Eip2935BlockNumber = transitionBlock;
            var result = Execute(currentNumber, 100000, code);
            result.StatusCode.Should().Be(1);

            StorageCell storageCell = new StorageCell(IVirtualMachine.BlockhashStorage, (UInt256) requestedNumber);
            if (useEip2935)
            {
                if (requestedNumber >= transitionBlock)
                {
                    AssertStorage(0, Storage.Get(storageCell));    
                }
                else
                {
                    AssertStorage(0, Bytes.Empty);    
                }
            }
            else
            {
                AssertStorage(0, BlockhashProvider.GetBlockhash(null, requestedNumber));
            }
        }
    }
}