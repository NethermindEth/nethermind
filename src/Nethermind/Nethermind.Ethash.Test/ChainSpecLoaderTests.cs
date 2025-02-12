// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Ethash.Test;

public class ChainSpecLoaderTests
{
    private static ChainSpec LoadChainSpec(string path)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_hive()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/hive.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Name, Is.EqualTo("Foundation"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("ethereum"), $"{nameof(chainSpec.Name)}");

        var parametrs = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<EthashChainSpecEngineParameters>();

        Assert.That(parametrs.MinimumDifficulty, Is.EqualTo((UInt256)0x020000), $"{nameof(parametrs.MinimumDifficulty)}");
        Assert.That(parametrs.DifficultyBoundDivisor, Is.EqualTo((long)0x0800), $"{nameof(parametrs.DifficultyBoundDivisor)}");
        Assert.That(parametrs.DurationLimit, Is.EqualTo(0xdL), $"{nameof(parametrs.DurationLimit)}");

        Assert.That(parametrs.BlockReward.Count, Is.EqualTo(3), $"{nameof(parametrs.BlockReward.Count)}");
        Assert.That(parametrs.BlockReward[0L], Is.EqualTo((UInt256)5000000000000000000));
        Assert.That(parametrs.BlockReward[4370000L], Is.EqualTo((UInt256)3000000000000000000));
        Assert.That(parametrs.BlockReward[7080000L], Is.EqualTo((UInt256)2000000000000000000));

        Assert.That(parametrs.DifficultyBombDelays.Count, Is.EqualTo(2), $"{nameof(parametrs.DifficultyBombDelays.Count)}");
        Assert.That(parametrs.DifficultyBombDelays[4370000], Is.EqualTo(3000000L));
        Assert.That(parametrs.DifficultyBombDelays[7080000], Is.EqualTo(2000000L));

        Assert.That(parametrs.HomesteadTransition, Is.EqualTo(0L));
        Assert.That(parametrs.DaoHardforkTransition, Is.EqualTo(1920000L));
        Assert.That(parametrs.DaoHardforkBeneficiary, Is.EqualTo(new Address("0xbf4ed7b27f1d666546e30d74d50d173d20bca754")));
        Assert.That(parametrs.DaoHardforkAccounts.Length, Is.EqualTo(0));
        Assert.That(parametrs.Eip100bTransition, Is.EqualTo(0L));

        Assert.That(chainSpec.ChainId, Is.EqualTo(1), $"{nameof(chainSpec.ChainId)}");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(1), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Genesis, Is.Not.Null, $"{nameof(ChainSpec.Genesis)}");

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"initial base fee value");
        Assert.That(chainSpec.Parameters.Eip1559ElasticityMultiplier, Is.EqualTo((long)1), $"elasticity multiplier");
        Assert.That(chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)7), $"base fee max change denominator");
        Assert.That(chainSpec.Genesis.BaseFeePerGas, Is.EqualTo((UInt256)11), $"genesis base fee");

        Assert.That(chainSpec.Genesis.Header.Nonce, Is.EqualTo(0xdeadbeefdeadbeef), $"genesis {nameof(BlockHeader.Nonce)}");
        Assert.That(chainSpec.Genesis.Header.MixHash, Is.EqualTo(Keccak.Zero), $"genesis {nameof(BlockHeader.MixHash)}");
        Assert.That((long)chainSpec.Genesis.Header.Difficulty, Is.EqualTo(0x10), $"genesis {nameof(BlockHeader.Difficulty)}");
        Assert.That(chainSpec.Genesis.Header.Beneficiary, Is.EqualTo(Address.Zero), $"genesis {nameof(BlockHeader.Beneficiary)}");
        Assert.That((long)chainSpec.Genesis.Header.Timestamp, Is.EqualTo(0x00L), $"genesis {nameof(BlockHeader.Timestamp)}");
        Assert.That(chainSpec.Genesis.Header.ParentHash, Is.EqualTo(Keccak.Zero), $"genesis {nameof(BlockHeader.ParentHash)}");
        Assert.That(
            chainSpec.Genesis.Header.ExtraData, Is.EqualTo(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000")),
            $"genesis {nameof(BlockHeader.ExtraData)}");
        Assert.That(chainSpec.Genesis.Header.GasLimit, Is.EqualTo(0x8000000L), $"genesis {nameof(BlockHeader.GasLimit)}");

        Assert.That(chainSpec.Allocations, Is.Not.Null, $"{nameof(ChainSpec.Allocations)}");
        Assert.That(chainSpec.Allocations.Count, Is.EqualTo(1), $"allocations count");
        Assert.That(
            chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Balance, Is.EqualTo(new UInt256(0xf4240)),
            "account 0x71562b71999873db5b286df957af199ec94617f7 - balance");

        Assert.That(
            chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Code, Is.EqualTo(Bytes.FromHexString("0xabcd")),
            "account 0x71562b71999873db5b286df957af199ec94617f7 - code");

        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        Assert.That(chainSpec.HomesteadBlockNumber, Is.EqualTo((long?)0), "homestead transition");
        Assert.That(chainSpec.TangerineWhistleBlockNumber, Is.EqualTo((long?)0), "tangerine whistle transition");
        Assert.That(chainSpec.SpuriousDragonBlockNumber, Is.EqualTo((long?)0), "spurious dragon transition");
        Assert.That(chainSpec.ByzantiumBlockNumber, Is.EqualTo((long?)0), "byzantium transition");
        Assert.That(chainSpec.DaoForkBlockNumber, Is.EqualTo((long?)1920000), "dao transition");
        Assert.That(chainSpec.ConstantinopleFixBlockNumber, Is.EqualTo((long?)7080000), "constantinople transition");

        Assert.That(chainSpec.Parameters.MaxCodeSize, Is.EqualTo((long?)24576L), "max code size");
        Assert.That(chainSpec.Parameters.MaxCodeSizeTransition, Is.EqualTo((long?)0L), "max code size transition");
        Assert.That(chainSpec.Parameters.MinGasLimit, Is.EqualTo((long?)0x1388L), "min gas limit");
        Assert.That(chainSpec.Parameters.Registrar, Is.EqualTo(new Address("0xe3389675d0338462dC76C6f9A3e432550c36A142")), "registrar");
        Assert.That(chainSpec.Parameters.ForkBlock, Is.EqualTo((long?)0x1d4c00L), "fork block");
        Assert.That(chainSpec.Parameters.ForkCanonHash, Is.EqualTo(new Hash256("0x4985f5ca3d2afbec36529aa96f74de3cc10a2a4a6c44f2157a57d2c6059a11bb")), "fork block");

        Assert.That(chainSpec.Parameters.Eip150Transition, Is.EqualTo((long?)0L), "eip150");
        Assert.That(chainSpec.Parameters.Eip160Transition, Is.EqualTo((long?)0L), "eip160");
        Assert.That(chainSpec.Parameters.Eip161abcTransition, Is.EqualTo((long?)0L), "eip161abc");
        Assert.That(chainSpec.Parameters.Eip161dTransition, Is.EqualTo((long?)0L), "eip161d");
        Assert.That(chainSpec.Parameters.Eip155Transition, Is.EqualTo((long?)0L), "eip155");
        Assert.That(chainSpec.Parameters.Eip140Transition, Is.EqualTo((long?)0L), "eip140");
        Assert.That(chainSpec.Parameters.Eip211Transition, Is.EqualTo((long?)0L), "eip211");
        Assert.That(chainSpec.Parameters.Eip214Transition, Is.EqualTo((long?)0L), "eip214");
        Assert.That(chainSpec.Parameters.Eip658Transition, Is.EqualTo((long?)0L), "eip658");
        Assert.That(chainSpec.Parameters.Eip145Transition, Is.EqualTo((long?)7080000L), "eip145");
        Assert.That(chainSpec.Parameters.Eip1014Transition, Is.EqualTo((long?)7080000L), "eip1014");
        Assert.That(chainSpec.Parameters.Eip1052Transition, Is.EqualTo((long?)7080000L), "eip1052");
        Assert.That(chainSpec.Parameters.Eip1283Transition, Is.EqualTo((long?)7080000L), "eip1283");

        Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo((long)32), "extra data");
        Assert.That(chainSpec.Parameters.GasLimitBoundDivisor, Is.EqualTo((long)0x0400), "gas limit bound divisor");
    }
}
