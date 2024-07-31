// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class Vulnerabilities
{
        public static Transaction[] GetTxs(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
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
            .WithData(PrepareCode(privateKey, blockGasConsumptionTarget))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject
        ];
    }

    private static byte[] PrepareCode(PrivateKey privateKey, long blockGasConsumptionTarget)
    {
        List<byte> codeToDeploy = new();

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        Transaction tx = Build.A.Transaction
            .WithNonce(UInt256.Zero)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(TestItem.AddressA)
            .WithChainId(BlockchainIds.Holesky)
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;

        byte[] contractCode = Bytes.FromHexString("3d4a3d4a335afa19380a3d4a3e68013789fa3a4a3a3d4a3d4a3d304a443038380136013801fa514a3d3038380138013801fa513d463d3038380138013801fa3d4a3d3d3038380138013801fa513d6a01423d30303434011001053d4a444a3038380138013801fa513d4a3d3d303838018001f2623d3d304a3d3038380136013801fa514a01053d4a4a443038380138013801fa513d4a3d3d303838018001f2623d3d304a3d3038380138013801fa3038380138013801fa3d4a3d3d3038380138013801fa4a3d3d303838018001f25a3d3d304a3d30383801360168013789fa3a4a3a3d4a3d4a3d304a443038380136013801fa514a3d3038380138013801fa513d463d3038380138013801fa3d3d4a3d3038380138013801fa4a3d3d303838018001f25a3d3d304a3d3038380136013801fa514a3d3038380138013801fa513d3038380138013801fa3d4a3d3d3038380138013801fa4a3d3d303838018001f2523d304a3d30383801360138013801fa513d3d3d3038380138013801fa3d4a3d3d3038380138013801fa513d6a01423d30303435013001053d4a444a3038380138013801fa513d4a3d3d303838018001f2623d3d304a3d3038380136013801fa514a01053d4a4a443038380138013801fa513d4a3d3d303838018001f2623d3d304a3d3038380138013401fa3038380138013801fa3d4a3d3d3038380138013801fa4a3d4a513d4a3d3d303838018001f2623d3d304a3d3038380136013801fa514a3d3038380138013801fa513d3d3d30363d363d5a305af419580a383d3d383d305a19f4380a593d5a305afa19580affffffffffff01000000fd0000d5feffff67fa19585a305afa19580a3d363d5a305af419580a383d5a305afa19580a36383680ffffffff3d5a3b3a5a3a3d36363d0affffffffffff01000000fd0000d5feffff6767b898959b380006513dca3d3d30384a305af4003d3036383680ffffffff3d5a3b3a5a3a3d3600363d3d003030f4");

        codeToDeploy.AddRange(contractCode);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
