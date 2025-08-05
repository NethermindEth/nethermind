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
            case TestCase.EcPairing1Pair:
                return PrepareCode(1, Bytes.FromHexString(""));
            case TestCase.EcPairing2Pairs:
                return PrepareCode(1, Bytes.FromHexString("0x2371e7d92e9fc444d0e11526f0752b520318c80be68bf0131704b36b7976572e2dca8f05ed5d58e0f2e13c49ae40480c0f99dfcd9268521eea6c81c6387b66c4051a93d697db02afd3dcf8414ecb906a114a2bfdb6b06c95d41798d1801b3cbd2e275fef7a0bdb0a2aea77d8ec5817e66e199b3d55bc0fa308dcdda74e85060b1c7e33c2a72d6e12a31eababad3dbc388525135628102bb64742d9e325f43410115dc41fa10b2dbf99036f252ad6f00e8876b22f02cb4738dc4413b22ea9b2df09a760ea8f9bd87dc258a949395a03f7d2500c6e72c61f570986328a096b610a148027063c072345298117eb2cb980ad79601db31cc69bba6bcbe4937ada6720198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa"));
            case TestCase.EcPairing3Pairs:
                return PrepareCode(1, Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ef4aac9b7954d5fc6eafae7f4f4c2a732ab05b45f8d50d102cee4973f36eb2c23db7d30c99e0a2a7f3bb5cd1f04635aaea58732b58887df93d9239c28230d282bd99d31a5054f2556d226f2e5ef0e075423d8604178b2e2c08006311caee54f0f11afb0c6073d12d21b13f4f78210e8ca9a66729206d3fcc2c1b04824c425f200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa"));
            case TestCase.EcPairing4Pairs:
                return PrepareCode(1, Bytes.FromHexString("24ab69f46f3e3333027d67d51af71571141bd5652b9829157a3c5d12684619840f0e1495665bccf97d627b714e8a49e9c77c21e8d5b383ad7dde7e50040d0f622cab595b9d579f8b82e433249b83ae1d7b62d7073a4f67cb3aeb9b316988907f1326d1905ffde0c77e8ebd98257aa239b05ae76c8ec7723ec19bbc8282b0debe130502106676b537e01cc356765e91c005d6c4bd1a75f5f6d41d2556c73e56ac2dc4cb08068b4aa5f14b7f1096ab35d5c13d78319ec7e66e9f67a1ff20cbbf031459f4140b271cbc8746de9dfcb477d5b72d50ef95bec5fef4a68dd69ddfdb2e2c589584551d16a9723b5d356d1ee2066d10381555cdc739e39efca2612fc544229ab0abdb0a7d1a5f0d93fb36ce41e12a31ba52fd9e3c27bebce524ab6c4e9b00f8756832b244377d06e2d00eeb95ec8096dcfd81f4e4931b50fea23c04a2fe29605352ce973ec48d1ab2c8355643c999b70ff771946078b519c556058c3d56059a65ae6e0189d4e04a966140aa40f781a1345824a90a91bb035e12ad29af1d1459f4140b271cbc8746de9dfcb477d5b72d50ef95bec5fef4a68dd69ddfdb2e2c589584551d16a9723b5d356d1ee2066d10381555cdc739e39efca2612fc544229ab0abdb0a7d1a5f0d93fb36ce41e12a31ba52fd9e3c27bebce524ab6c4e9b00f8756832b244377d06e2d00eeb95ec8096dcfd81f4e4931b50fea23c04a2fe29605352ce973ec48d1ab2c8355643c999b70ff771946078b519c556058c3d56059a65ae6e0189d4e04a966140aa40f781a1345824a90a91bb035e12ad29af1d24ab69f46f3e3333027d67d51af71571141bd5652b9829157a3c5d12684619840f0e1495665bccf97d627b714e8a49e9c77c21e8d5b383ad7dde7e50040d0f622cab595b9d579f8b82e433249b83ae1d7b62d7073a4f67cb3aeb9b316988907f1326d1905ffde0c77e8ebd98257aa239b05ae76c8ec7723ec19bbc8282b0debe130502106676b537e01cc356765e91c005d6c4bd1a75f5f6d41d2556c73e56ac2dc4cb08068b4aa5f14b7f1096ab35d5c13d78319ec7e66e9f67a1ff20cbbf03"));
            case TestCase.EcPairing5Pairs:
                return PrepareCode(1, Bytes.FromHexString("1147057b17237df94a3186435acf66924e1d382b8c935fdd493ceb38c38def7303cd046286139915160357ce5b29b9ea28bfb781b71734455d20ef1a64be76ca0daa7cc4983cf74c94607519df747f61e317307c449bafb6923f6d6a65299a7e1d48db8f275830859fd61370addbc5d5ef3f0ce7491d16918e065f7e3727439d1ca8ac2f4a0f540e5505edbe1d15d13899a2a0dfccb012d068134ac66edec6252162c315417d1d12c9d7028c5619015391003a9006d4d8979784c7af2c4537a30d221a19ca86dafa8cb804daff78fd3d1bed30aa32e7d4029b1aa69afda2d750018628c766a98de1d0cca887a6d90303e68a7729490f25f937b76b57624ba0be14550ccf7139312da6fa9eb1259c6365b0bd688a27473ccb42bc5cd6f14c8abd165f8721ee9f614382c8c7edb103c941d3a55c1849c9787f34317777d5d9365b0d19da7439edb573a1b3e357faade63d5d68b6031771fd911459b7ab0bda9d3f25a50a44d10c99c5f107e3b3874f717873cb2d4674699a468204df27c0c50a9a0d7136c59b907615e1b45cf730fbfd6cf38b7e126e85e52be804620a23ace4fb03e80c29d24ed5cc407329ae093bb1be00f9e3c9332f532bc3658937110d76072129813bd7247065ac58eac42c81e874044e199f48c12aa749a9fe6bb6e4bddc1b72b9ab4579283e62445555d5b2921424213d09a776152361c46988b82be8a7111bc8198f932e379b8f9825f01af0f5e5cacbf8bfe274bf674f6eaa6e338e04259f58d438fd6391e158c991e155966218e6a432703a84068a325439657498571ba47a91d487cce77aa78390a295df54d9351637d67810c400415fb374278e3f24318bbc05a4e4d779b9498075841c360c6973c1c51dea254281829bbc9aef33198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa1e219772c16eee72450bbf43e9cadae7bf6b2e6ae6637cfeb1d1e8965287acfb0347e7bf4245debd3d00b6f51d2d50fd718e6769352f4fe1db0efe492fed2fc324fdcc7d4ed0953e3dad500c7ef9836fc61ded44ba454ec76f0a6d0687f4c1b4282b18f7e59c1db4852e622919b2ce9aa5980ca883eac312049c19a3deb79f6d0c9d6ce303b7811dd7ea506c8fa124837405bd209b8731bda79a66eb7206277b1ac5dac62d2332faa8069faca3b0d27fcdf95d8c8bafc9074ee72b5c1f33aa70"));
            case TestCase.EcPairing10Pairs:
                return PrepareCode(1, Bytes.FromHexString(""));
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
