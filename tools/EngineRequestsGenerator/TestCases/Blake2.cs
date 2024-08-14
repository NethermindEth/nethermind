// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class Blake2
{
    public static Transaction[] GetTxs(TestCase testCase, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        return
        [
            Build.A.Transaction
                .WithNonce((UInt256)nonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareCode(testCase, blockGasConsumptionTarget))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(TestCase testCase, long blockGasConsumptionTarget)
    {
        List<byte> codeToDeploy = new();

        byte[] numberOfRounds = testCase switch
        {
            TestCase.Blake1Round => 1.ToByteArray(),
            TestCase.Blake1KRounds => 1000.ToByteArray(),
            TestCase.Blake1MRounds => 1_000_000.ToByteArray(),
            TestCase.Blake10MRounds => 10_000_000.ToByteArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null)
        };

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();


        codeToDeploy.Add((byte)Instruction.PUSH1);                      // rounds - 4 bytes
        codeToDeploy.Add(numberOfRounds[0]);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE8);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(numberOfRounds[1]);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)1);
        codeToDeploy.Add((byte)Instruction.MSTORE8);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(numberOfRounds[2]);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)2);
        codeToDeploy.Add((byte)Instruction.MSTORE8);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(numberOfRounds[3]);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)3);
        codeToDeploy.Add((byte)Instruction.MSTORE8);


        byte[] stateVector = new byte[64];
        for (byte i = 0; i < stateVector.Length; i++)
        {
            stateVector[i] = i;
        }
        codeToDeploy.Add((byte)Instruction.PUSH32);                     // state vector - 64 bytes
        codeToDeploy.AddRange(stateVector.Slice(0, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)4);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(stateVector.Slice(32, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)36);
        codeToDeploy.Add((byte)Instruction.MSTORE);


        byte[] messageBlockVector = new byte[128];
        for (byte i = 0; i < messageBlockVector.Length; i++)
        {
            messageBlockVector[i] = i;
        }
        codeToDeploy.Add((byte)Instruction.PUSH32);                     // message block vector - 128 bytes
        codeToDeploy.AddRange(messageBlockVector.Slice(0, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)68);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(messageBlockVector.Slice(32, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)100);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(messageBlockVector.Slice(64, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)132);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(messageBlockVector.Slice(96, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)164);
        codeToDeploy.Add((byte)Instruction.MSTORE);


        for (byte i = 0; i < 16; i++)                                       // offset counters - 16 bytes
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add((byte)i);
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add((byte)(196 + i));
            codeToDeploy.Add((byte)Instruction.MSTORE8);
        }


        codeToDeploy.Add((byte)Instruction.PUSH1);                     // final block indicator - 1 byte
        codeToDeploy.Add((byte)1);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)212);
        codeToDeploy.Add((byte)Instruction.MSTORE8);


        long jumpDestPosition = codeToDeploy.Count;
        byte[] jumpDestBytes = jumpDestPosition.ToBigEndianByteArrayWithoutLeadingZeros();
        codeToDeploy.Add((byte)Instruction.JUMPDEST);
        Console.WriteLine($"jumpdest: {jumpDestPosition}");

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add((byte)64);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add((byte)213);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add((byte)213);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add((byte)9);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)jumpDestBytes.Length - 1));
        codeToDeploy.AddRange(jumpDestBytes);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
