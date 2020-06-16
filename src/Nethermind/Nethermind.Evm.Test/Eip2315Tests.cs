// //  Copyright (c) 2018 Demerzel Solutions Limited
// //  This file is part of the Nethermind library.
// // 
// //  The Nethermind library is free software: you can redistribute it and/or modify
// //  it under the terms of the GNU Lesser General Public License as published by
// //  the Free Software Foundation, either version 3 of the License, or
// //  (at your option) any later version.
// // 
// //  The Nethermind library is distributed in the hope that it will be useful,
// //  but WITHOUT ANY WARRANTY; without even the implied warranty of
// //  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// //  GNU Lesser General Public License for more details.
// // 
// //  You should have received a copy of the GNU Lesser General Public License
// //  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//
// using Nethermind.Core.Crypto;
// using Nethermind.Core.Extensions;
// using Nethermind.Core.Specs;
// using Nethermind.Specs;
// using Nethermind.Specs.Forks;
// using Nethermind.Core.Test.Builders;
// using Nethermind.Dirichlet.Numerics;
// using Nethermind.State;
// using Nethermind.Db.Blooms;
// using NUnit.Framework;
//
// namespace Nethermind.Evm.Test
// {
//     public class Eip1884Tests : VirtualMachineTestsBase
//     {
//         protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
//         protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;
//
//         [Test]
//         public void after_istanbul_extcodehash_cost_is_increased()
//         {
//             TestState.CreateAccount(TestItem.AddressC, 100.Ether());
//
//             byte[] code = Prepare.EvmCode
//                 .PushData(TestItem.AddressC)
//                 .Op(Instruction.EXTCODEHASH)
//                 .Done;
//
//             var result = Execute(code);
//             AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.ExtCodeHashEip1884);
//         }
//     }
// }