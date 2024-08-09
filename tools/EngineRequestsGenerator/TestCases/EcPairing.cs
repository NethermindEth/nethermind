// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class EcPairing
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
                .WithData(PrepareCode(testCase))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(TestCase testCase)
    {
        switch (testCase)
        {
            case TestCase.EcPairing0Input:
                return PrepareCode(0, []);
            case TestCase.EcPairing2Sets:
                return PrepareCode(1, Bytes.FromHexString("2cf44499d5d27bb186308b7af7af02ac5bc9eeb6a3d147c186b21fb1b76e18da2c0f001f52110ccfe69108924926e45f0b0c868df0e7bde1fe16d3242dc715f61fb19bb476f6b9e44e2a32234da8212f61cd63919354bc06aef31e3cfaff3ebc22606845ff186793914e03e21df544c34ffe2f2f3504de8a79d9159eca2d98d92bd368e28381e8eccb5fa81fc26cf3f048eea9abfdd85d7ed3ab3698d63e4f902fe02e47887507adf0ff1743cbac6ba291e66f59be6bd763950bb16041a0a85e000000000000000000000000000000000000000000000000000000000000000130644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd451971ff0471b09fa93caaf13cbf443c1aede09cc4328f5a62aad45f40ec133eb4091058a3141822985733cbdddfed0fd8d6c104e9e9eff40bf5abfef9ab163bc72a23af9a5ce2ba2796c1f4e453a370eb0af8c212d9dc9acd8fc02c2e907baea223a8eb0b0996252cb548a4487da97b02422ebc0e834613f954de6c7e0afdc1fc"));
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }
        private static byte[] PrepareCode(int iterations, byte[] inputData)
    {
        List<byte> codeToDeploy = new();

        long offset = 0;
        for (int i = 0; i < inputData.Length; i += 32)
        {
            byte[] innerOffset = offset.ToBigEndianByteArrayWithoutLeadingZeros();

            codeToDeploy.Add((byte)Instruction.PUSH32);
            codeToDeploy.AddRange(inputData.Slice(i, 32));
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)innerOffset.Length - 1));
            codeToDeploy.AddRange(innerOffset);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }

        long jumpDestPosition = codeToDeploy.Count;
        byte[] jumpDestBytes = jumpDestPosition.ToBigEndianByteArrayWithoutLeadingZeros();
        codeToDeploy.Add((byte)Instruction.JUMPDEST);
        Console.WriteLine($"jumpdest: {jumpDestPosition}");

        long gasLimit = 45_000 + 34_000 * (offset / 192);
        byte[] gasLimitBytes = gasLimit.ToBigEndianByteArrayWithoutLeadingZeros();

        for (int i = 0; i < 1000; i++)
        {
            byte[] innerOffset = offset.ToBigEndianByteArrayWithoutLeadingZeros();

            codeToDeploy.Add((byte)Instruction.PUSH1);                                      // return size
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)innerOffset.Length - 1));     // return offset
            codeToDeploy.AddRange(innerOffset);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)innerOffset.Length - 1));     // args size
            codeToDeploy.AddRange(innerOffset);
            codeToDeploy.Add((byte)Instruction.PUSH0);                                      // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);                                      // address
            codeToDeploy.Add(0x08);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasLimitBytes.Length - 1));
            codeToDeploy.AddRange(gasLimitBytes);
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
