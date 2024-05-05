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
// public class Sha2
// {
//     public Transaction GetTx(PrivateKey privateKey, int nonce, TestCase testCase, long blockGasConsumptionTarget)
//     {
//         return Build.A.Transaction
//             .WithNonce((UInt256)nonce)
//             .WithType(TxType.EIP1559)
//             .WithMaxFeePerGas(1.GWei())
//             .WithMaxPriorityFeePerGas(1.GWei())
//             .WithTo(null)
//             .WithChainId(BlockchainIds.Holesky)
//             .WithData(PrepareSha2Code(blockGasConsumptionTarget, 32))
//             .WithGasLimit(blockGasConsumptionTarget)
//             .SignedAndResolved(privateKey)
//             .TestObject;
//     }
//
//     private byte[] PrepareSha2Code(long blockGasConsumptionTarget, int i)
//     {
//        List<byte> byteCode = new();
//
//         long gasLeft = blockGasConsumptionTarget - GasCostOf.Transaction;
//         // long gasCost = GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in length);
//         // long iterations = (blockGasConsumptionTarget - GasCostOf.Transaction) / gasCost;
//
//         long gasCost = 0;
//         long dataCost = 0;
//
//         List<byte> preIterationCode = new();
//
//         preIterationCode.Add((byte)(Instruction.PUSH0));
//         gasCost += GasCostOf.Base;
//
//         List<byte> iterationCode = new();
//         long gasCostPerIteration = 0;
//
//         // push memory position - 0
//         iterationCode.Add((byte)(Instruction.PUSH1));
//         gasCostPerIteration += GasCostOf.VeryLow;
//         iterationCode.AddRange(new[] { Byte.MinValue });
//         // save in memory
//         iterationCode.Add((byte)Instruction.MSTORE);
//         gasCostPerIteration += GasCostOf.Memory;
//
//         // push byte size to read from memory - bytesToComputeKeccak
//         iterationCode.Add((byte)(Instruction.PUSH1));
//         gasCostPerIteration += GasCostOf.VeryLow;
//         iterationCode.AddRange(new[] { (byte)bytesToComputeKeccak });
//         // push byte offset in memory - 0
//         iterationCode.Add((byte)(Instruction.PUSH1));
//         gasCostPerIteration += GasCostOf.VeryLow;
//         iterationCode.AddRange(new[] { Byte.MinValue });
//         // compute keccak
//         iterationCode.Add((byte)Instruction.KECCAK256);
//         gasCostPerIteration += GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(1);
//
//         // now keccak of given data is on top of stack
//
//
//         // int zeroData = iterationCode.ToArray().AsSpan().CountZeros();
//         //
//         // dataCost += zeroData * GasCostOf.TxDataZero + (iterationCode.Count - zeroData) * GasCostOf.TxDataNonZeroEip2028;
//         // gasCost += dataCost;
//
//
//         gasLeft -= gasCost;
//
//         long iterations = (_chainSpecBasedSpecProvider.GenesisSpec.MaxInitCodeSize - preIterationCode.Count) / iterationCode.Count;
//
//         byteCode.AddRange(preIterationCode);
//
//         for (int i = 0; i < iterations; i++)
//         {
//             byteCode.AddRange(iterationCode);
//             gasCost += gasCostPerIteration;
//         }
//
//         int zeroData = byteCode.ToArray().AsSpan().CountZeros();
//         dataCost += zeroData * GasCostOf.TxDataZero + (byteCode.Count - zeroData) * GasCostOf.TxDataNonZeroEip2028;
//
//         gasCost += dataCost;
//
//         return byteCode.ToArray();
//     }
// }
