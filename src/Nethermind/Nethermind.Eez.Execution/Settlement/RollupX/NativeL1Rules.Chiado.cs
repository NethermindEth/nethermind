// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeL1Rules
{
    private const ulong ChiadoMergeBlock = 680_930;
    private const int MaxDocumentBytes = 8 * 1024 * 1024;

    internal static NativeL1Rules Load(string source)
    {
        if (source == "chiado") return LoadChiado();
        using FileStream file = File.OpenRead(source);
        if (file.Length > MaxDocumentBytes) throw Invalid("L1 genesis exceeds the size limit");
        byte[] encoded = new byte[checked((int)file.Length)];
        file.ReadExactly(encoded);
        if (file.ReadByte() != -1) throw Invalid("L1 genesis changed while loading");
        return LoadEthereum(encoded);
    }

    private static NativeL1Rules LoadChiado()
    {
        using Stream input = typeof(IConfig).Assembly.GetManifestResourceStream("Nethermind.Config.chainspec.chiado.json")
            ?? throw Invalid("missing embedded Chiado preset");
        using MemoryStream original = new();
        input.CopyTo(original);
        byte[] source = original.ToArray();
        ValueHash256 digest = ValueKeccak.Compute(RollupXCanonicalJson.Encode(source).Span);
        EthereumJsonSerializer serializer = new();
        original.Position = 0;
        ChainSpecJson preset = serializer.Deserialize<ChainSpecJson>(original) ?? throw Invalid("invalid embedded Chiado preset");
        ChainSpecParamsJson parameters = preset.Params ?? throw Invalid("missing Chiado parameters");
        if ((parameters.ChainId ?? parameters.NetworkId) != BlockchainIds.Chiado) throw Invalid("embedded Chiado chain identity differs");
        GethGenesisConfigJson config = new()
        {
            ChainId = BlockchainIds.Chiado,
            HomesteadBlock = 0,
            Eip150Block = 0,
            Eip155Block = 0,
            ByzantiumBlock = 0,
            ConstantinopleBlock = 0,
            PetersburgBlock = 0,
            IstanbulBlock = 0,
            BerlinBlock = 0,
            LondonBlock = 0,
            TerminalTotalDifficulty = parameters.TerminalTotalDifficulty,
            ShanghaiTime = parameters.Eip4895TransitionTimestamp,
            CancunTime = parameters.Eip4844TransitionTimestamp,
            PragueTime = parameters.Eip7002TransitionTimestamp,
            OsakaTime = parameters.Eip7594TransitionTimestamp,
            DepositContractAddress = parameters.DepositContractAddress,
        };
        GethGenesisJson genesis = ChiadoExecutionGenesis(preset, config);
        using MemoryStream encoded = new();
        serializer.Serialize(encoded, genesis);
        encoded.Position = 0;
        // The network profile commits an execution-envelope genesis; L1 evidence is post-merge only.
        ChainSpec spec = new GethGenesisLoader(serializer).Load(encoded);
        spec.Parameters.FeeCollector = parameters.FeeCollector;
        spec.Parameters.Eip1559FeeCollectorTransition = parameters.Eip1559FeeCollectorTransition;
        spec.Parameters.Eip4844FeeCollectorTransitionTimestamp = parameters.Eip4844FeeCollectorTransitionTimestamp;
        spec.Parameters.Eip4844MinBlobGasPrice = parameters.Eip4844MinBlobGasPrice;
        return new NativeL1Rules(spec, in digest, config);
    }

    private static GethGenesisJson ChiadoExecutionGenesis(ChainSpecJson preset, GethGenesisConfigJson config)
    {
        ChainSpecGenesisJson genesis = preset.Genesis ?? throw Invalid("missing Chiado genesis");
        Dictionary<Address, GethGenesisAllocJson> allocations = [];
        foreach ((string address, AllocationJson account) in preset.Accounts ?? throw Invalid("missing Chiado allocations"))
        {
            if (account.BuiltIn is not null && account.Balance is null) continue;
            if (account.Constructor is not null || account.CodeHash is not null) throw Invalid("unsupported Chiado allocation");
            allocations[new Address(address)] = new GethGenesisAllocJson
            {
                Balance = account.Balance,
                Nonce = account.Nonce,
                Code = account.Code,
                Storage = account.GetConvertedStorage(),
            };
        }
        return new GethGenesisJson
        {
            Config = config,
            Alloc = allocations,
            Difficulty = genesis.Difficulty,
            GasLimit = genesis.GasLimit,
            Timestamp = genesis.Timestamp,
            ExtraData = genesis.ExtraData,
            Coinbase = genesis.Author,
            BaseFeePerGas = genesis.BaseFeePerGas,
        };
    }
}
