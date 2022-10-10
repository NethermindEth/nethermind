#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle.Json;
using Newtonsoft.Json.Linq;
using static Nethermind.Specs.ChainSpecStyle.Json.ChainSpecJson;

namespace Nethermind.Specs.GethSpecStyle.Json
{
    internal class GethSpecConverter
    {
        // Note : match Parity Chainspec loader defaults
        public static ChainSpecJson? ToParityChainsSpec(GethGenesisJson gethGenesis)
        {
            var parityName = $"GethConvertedSpec{gethGenesis.Config?.ChainId}";

            EngineJson engine = ExtractEngine(gethGenesis);
            ChainSpecParamsJson parameters = ExtractChainParameters(gethGenesis);
            ChainSpecGenesisJson genesis = ExtractGenesisBlock(gethGenesis);
            Dictionary<string, AllocationJson>? accounts = ExtractAccount(gethGenesis);

            return new ChainSpecJson
            {
                Name = parityName,
                Engine = engine,
                Params = parameters,
                Genesis = genesis,
                Accounts = accounts
            };
        }
        private static EngineJson ExtractEngine(GethGenesisJson gethGenesis)
        {
            return new EngineJson
            {
                Ethash = gethGenesis.Config?.Ethash is null
                    ? null
                    : new EthashEngineJson
                    {
                        Params = new EthashEngineParamsJson
                        {
                            DaoHardforkTransition = gethGenesis.Config?.DaoForkBlock,
                            HomesteadTransition = gethGenesis.Config?.HomesteadBlock ?? 0,
                            Eip100bTransition = gethGenesis.Config?.ByzantiumBlock ?? 0,
                            MinimumDifficulty = 131072,
                        }
                    },
                Clique = gethGenesis.Config?.Clique is null
                    ? null
                    : new CliqueEngineJson
                    {
                        Params = new CliqueEngineParamsJson
                        {
                            Period = gethGenesis.Config?.Clique.Period ?? 0,
                            Epoch = gethGenesis.Config?.Clique.Epoch ?? 0,
                        }
                    },
                NethDev = gethGenesis.Config?.NethDev is null
                    ? null
                    : new NethDevJson()
            };
        }

        private static ChainSpecParamsJson ExtractChainParameters(GethGenesisJson gethGenesis)
        {
            return new ChainSpecParamsJson
            {
                ChainId = gethGenesis.Config?.ChainId ?? 0,
                NetworkId = gethGenesis.Config?.ChainId ?? 0,

                Eip1283DisableTransition = gethGenesis.Config?.PetersburgBlock,
                Eip160Transition  = gethGenesis.Config?.Eip160Block,
                Eip150Transition  = gethGenesis.Config?.TangerineWhistleBlock ?? gethGenesis.Config?.Eip150Block,
                Eip140Transition  = gethGenesis.Config?.ByzantiumBlock,
                Eip1283Transition = gethGenesis.Config?.PetersburgBlock,
                Eip145Transition  = gethGenesis.Config?.ConstantinopleBlock,
                Eip2200Transition = gethGenesis.Config?.IstanbulBlock,
                Eip2929Transition = gethGenesis.Config?.BerlinBlock ?? (long.MaxValue - 1),
                Eip1559Transition = gethGenesis.Config?.LondonBlock ?? (long.MaxValue - 1),
                Eip1153Transition = gethGenesis.Config?.ShanghaiBlock ?? (long.MaxValue - 1),
                Eip155Transition  = gethGenesis.Config?.Eip155Block,

                MergeForkIdTransition = gethGenesis.Config?.MergeNetSplitBlock,
                TerminalTotalDifficulty = gethGenesis.Config?.TerminalTotalDifficulty,
                TerminalPoWBlockNumber = gethGenesis.Config?.MergeNetSplitBlock - 1
            };
        }

        private static ChainSpecGenesisJson ExtractGenesisBlock(GethGenesisJson gethGenesis)
        {
            return new ChainSpecGenesisJson
            {
                Seal = new ChainSpecSealJson
                {
                    Ethereum = new ChainSpecEthereumSealJson
                    {
                        Nonce = gethGenesis.Nonce ?? 0,
                        MixHash = gethGenesis.Mixhash
                    }
                },
                Difficulty = gethGenesis.Difficulty ?? 0,
                Author = gethGenesis.Coinbase,
                Timestamp = gethGenesis.Timestamp ?? 0,
                ParentHash = gethGenesis.ParentHash,
                ExtraData = gethGenesis.ExtraData,
                GasLimit = (UInt256?)gethGenesis.GasLimit ?? 0
            };
        }

        private static Dictionary<string, AllocationJson>? ExtractAccount(GethGenesisJson gethGenesis)
        {
            static BuiltInJson? ToBuiltinJson(string allocKey, GethAllocationJson allocation) {
                var builtinNum = UInt256.Parse(allocKey.StartsWith("0x") ? allocKey.Substring(2) : allocKey, System.Globalization.NumberStyles.HexNumber);
                if (builtinNum == 1) return new BuiltInJson
                {
                    Name = "ecrecover",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 3000, word = 0 })

                    }
                };
                if (builtinNum == 2) return new BuiltInJson
                {
                    Name = "sha256",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 60, word = 12 })

                    }
                };
                if (builtinNum == 3) return new BuiltInJson
                {
                    Name = "ripemd160",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 600, word = 120 })

                    }
                };
                if (builtinNum == 4) return new BuiltInJson
                {
                    Name = "identity",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 15, word = 3 })

                    }
                };
                if (builtinNum == 5) return new BuiltInJson
                {
                    Name = "nodexp",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["modexp"] = JObject.FromObject(new { divisor = 20 })

                    }
                };
                if (builtinNum == 6) return new BuiltInJson
                {
                    Name = "alt_bn128_add",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 500, word = 0 })

                    }
                };
                if (builtinNum == 7) return new BuiltInJson
                {
                    Name = "alt_bn128_mul",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["linear"] = JObject.FromObject(new { @base = 4000, word = 0 })

                    }
                };
                if (builtinNum == 8) return new BuiltInJson
                {
                    Name = "alt_bn128_pairing",
                    Pricing = new Dictionary<string, JObject>
                    {
                        ["alt_bn128_pairing"] = JObject.FromObject(new { @base = 100000, pair = 80000 })

                    }
                };
                return null;
            }
            return gethGenesis
                    ?.Alloc
                    ?.Select(allocPair => (allocPair.Key, new AllocationJson
                    {
                        Balance = allocPair.Value.Balance,
                        Nonce = allocPair.Value.Nonce ?? 0,
                        BuiltIn = ToBuiltinJson(allocPair.Key, allocPair.Value)
                    })).ToDictionary((pair) => pair.Key, (pair) => pair.Item2);
        }
    }
}
