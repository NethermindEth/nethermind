// // SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only
//
// using Nethermind.Core;
// using Nethermind.Core.Extensions;
// using Nethermind.Core.Test.Builders;
// using Nethermind.Crypto;
// using Nethermind.Evm;
// using Nethermind.Int256;
//
// namespace EngineRequestsGenerator.TestCases;
//
// public class BalanceNonExisting
// {
//     public static Transaction[] GetTxs(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
//     {
//         return
//         [
//             Build.A.Transaction
//                 .WithNonce((UInt256)nonce)
//                 .WithType(TxType.EIP1559)
//                 .WithMaxFeePerGas(1.GWei())
//                 .WithMaxPriorityFeePerGas(1.GWei())
//                 .WithTo(null)
//                 .WithChainId(BlockchainIds.Holesky)
//                 .WithData(PrepareCode())
//                 .WithGasLimit(blockGasConsumptionTarget)
//                 .SignedAndResolved(privateKey)
//                 .TestObject
//         ];
//     }
//
//     private static byte[] PrepareCode()
//     {
//         int iterations = 1001;
//         PrivateKey[] privateKeys = TestCaseGenerator.PreparePrivateKeys(iterations + 100).ToArray();
//
//         List<byte> codeToDeploy = new();
//
//         // codeToDeploy.Add((byte)Instruction.JUMPDEST);   // second item - jump destination (on offset 0)
//
//         for (int i = 0; i < iterations; i++)
//         {
//
//             codeToDeploy.Add((byte)Instruction.PUSH20);
//             codeToDeploy.AddRange(privateKeys[i + 100].Address.Bytes);
//             codeToDeploy.Add((byte)Instruction.BALANCE);
//         }
//
//         // codeToDeploy.Add((byte)Instruction.PUSH0);
//         // codeToDeploy.Add((byte)Instruction.JUMP);
//
//         List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
//         return byteCode.ToArray();
//     }
// }
