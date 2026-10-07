// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Spec;

public partial record BeaconChainSpec
{
    // https://github.com/gnosischain/configs/tree/2dd5746292ca298ae0695f1ee221098074574e04
    public static BeaconChainSpec Gnosis { get; } = new()
    {
        ChainId = BlockchainIds.Gnosis,
        CheckpointSyncUrl = "https://checkpoint.gnosischain.com",
        SecondsPerSlot = 5,
        SlotsPerEpoch = 16,
        GenesisTime = 1638993340,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0xf5dcb5564e829aab27264b9becd5dfaa017085611224cb3036f573368dbb9d47")),
        Forks =
        [
            new(Bytes.FromHexString("0x00000064"), 0),
            new(Bytes.FromHexString("0x01000064"), 512),
            new(Bytes.FromHexString("0x02000064"), 385536),
            new(Bytes.FromHexString("0x03000064"), 648704),
            new(Bytes.FromHexString("0x04000064"), 889856),
            new(Bytes.FromHexString("0x05000064"), 1337856),
            new(Bytes.FromHexString("0x06000064"), 1714688),
        ],
        BlobSchedule = [],
        ElectraForkEpoch = 1337856,
        FuluForkEpoch = 1714688,
        MaxBlobsPerBlockElectra = 2,
        GloasForkEpoch = Presets.FarFutureEpoch,
        GloasForkVersion = new byte[4],
        Bootnodes =
        [
            "enr:-Ly4QIAhiTHk6JdVhCdiLwT83wAolUFo5J4nI5HrF7-zJO_QEw3cmEGxC1jvqNNUN64Vu-xxqDKSM528vKRNCehZAfEBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpCCS-QxAgAAZP__________gmlkgnY0gmlwhEFtZ5SJc2VjcDI1NmsxoQJwgL5C-30E8RJmW8gCb7sfwWvvfre7wGcCeV4X1G2wJYhzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA",
            "enr:-Ly4QDhEjlkf8fwO5uWAadexy88GXZneTuUCIPHhv98v8ZfXMtC0S1S_8soiT0CMEgoeLe9Db01dtkFQUnA9YcnYC_8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpCCS-QxAgAAZP__________gmlkgnY0gmlwhEFtZ5WJc2VjcDI1NmsxoQMRSho89q2GKx_l2FZhR1RmnSiQr6o_9hfXfQUuW6bjMohzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA",
            "enr:-Ly4QLKgv5M2D4DYJgo6s4NG_K4zu4sk5HOLCfGCdtgoezsbfRbfGpQ4iSd31M88ec3DHA5FWVbkgIas9EaJeXia0nwBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpCCS-QxAgAAZP__________gmlkgnY0gmlwhI1eYRaJc2VjcDI1NmsxoQLpK_A47iNBkVjka9Mde1F-Kie-R0sq97MCNKCxt2HwOIhzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA",
            "enr:-Ly4QF_0qvji6xqXrhQEhwJR1W9h5dXV7ZjVCN_NlosKxcgZW6emAfB_KXxEiPgKr_-CZG8CWvTiojEohG1ewF7P368Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpCCS-QxAgAAZP__________gmlkgnY0gmlwhI1eYUqJc2VjcDI1NmsxoQIpNRUT6llrXqEbjkAodsZOyWv8fxQkyQtSvH4sg2D7n4hzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA",
            "enr:-Ly4QCD5D99p36WafgTSxB6kY7D2V1ca71C49J4VWI2c8UZCCPYBvNRWiv0-HxOcbpuUdwPVhyWQCYm1yq2ZH0ukCbQBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpCCS-QxAgAAZP__________gmlkgnY0gmlwhI1eYVSJc2VjcDI1NmsxoQJJMSV8iSZ8zvkgbi8cjIGEUVJeekLqT0LQha_co-siT4hzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA",
            "enr:-KK4QKXJq1QOVWuJAGige4uaT8LRPQGCVRf3lH3pxjaVScMRUfFW1eiiaz8RwOAYvw33D4EX-uASGJ5QVqVCqwccxa-Bi4RldGgykCGm-DYDAABk__________-CaWSCdjSCaXCEM0QnzolzZWNwMjU2azGhAhNvrRkpuK4MWTf3WqiOXSOePL8Zc-wKVpZ9FQx_BDadg3RjcIIjKIN1ZHCCIyg",
            "enr:-LO4QO87Rn2ejN3SZdXkx7kv8m11EZ3KWWqoIN5oXwQ7iXR9CVGd1dmSyWxOL1PGsdIqeMf66OZj4QGEJckSi6okCdWBpIdhdHRuZXRziAAAAABgAAAAhGV0aDKQPr_UhAQAAGT__________4JpZIJ2NIJpcIQj0iX1iXNlY3AyNTZrMaEDd-_eqFlWWJrUfEp8RhKT9NxdYaZoLHvsp3bbejPyOoeDdGNwgiMog3VkcIIjKA",
            "enr:-LK4QIJUAxX9uNgW4ACkq8AixjnSTcs9sClbEtWRq9F8Uy9OEExsr4ecpBTYpxX66cMk6pUHejCSX3wZkK2pOCCHWHEBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpA-v9SEBAAAZP__________gmlkgnY0gmlwhCPSnDuJc2VjcDI1NmsxoQNuaAjFE-ANkH3pbeBdPiEIwjR5kxFuKaBWxHkqFuPz5IN0Y3CCIyiDdWRwgiMo",
        ],
    };
    public static BeaconChainSpec Chiado { get; } = new()
    {
        ChainId = BlockchainIds.Chiado,
        CheckpointSyncUrl = "https://checkpoint.chiadochain.net",
        SecondsPerSlot = 5,
        SlotsPerEpoch = 16,
        GenesisTime = 1665396300,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0x9d642dac73058fbf39c0ae41ab1e34e4d889043cb199851ded7095bc99eb4c1e")),
        Forks =
        [
            new(Bytes.FromHexString("0x0000006f"), 0),
            new(Bytes.FromHexString("0x0100006f"), 90),
            new(Bytes.FromHexString("0x0200006f"), 180),
            new(Bytes.FromHexString("0x0300006f"), 244224),
            new(Bytes.FromHexString("0x0400006f"), 516608),
            new(Bytes.FromHexString("0x0500006f"), 948224),
            new(Bytes.FromHexString("0x0600006f"), 1353216),
        ],
        BlobSchedule = [],
        ElectraForkEpoch = 948224,
        FuluForkEpoch = 1353216,
        MaxBlobsPerBlockElectra = 2,
        GloasForkEpoch = Presets.FarFutureEpoch,
        GloasForkVersion = new byte[4],
        Bootnodes =
        [
            "enr:-IS4QC9xcSNOKeUBrjOX1-qwcPDGOvRK-Aql_YnRfuYsuXElQawANMLTOz1YfrZTMI07B7DAl9hmk3ic5G7ZHQbq4HsUgmlkgnY0gmlwhDNL9gKJc2VjcDI1NmsxoQOzB8HdKe07eQBOxu5nexAWECV47zxlau23oEYqoToy9YN1ZHCCIyg",
            "enr:-IS4QMpCWNqXmqbtXg7vQyYBwV2xTDpgTNpgAz060TY5cpJ3JdjuwAkLG01Vf9Vlfb0jtRhJEpj0CfteG-3K_V36bmECgmlkgnY0gmlwhDmBRt6Jc2VjcDI1NmsxoQNkoifSsk1ijP3-OR9pcL2VWCSfD5_syapyrXA83GcAvYN1ZHCCIyg",
            "enr:-IS4QBa3hl_BcjrY4z_OvhRBx7qUMBFGkjkoedGNMXgC_fzLRBsg161SipeHWAkUnSuvBg_7529-Qfxcc7CAbokn4fMCgmlkgnY0gmlwhDmA_HuJc2VjcDI1NmsxoQL8xX-V_pyU_l3AIfVokHKMN1OMUG02vcy4AMOxdyi5jIN1ZHCCIyg",
            "enr:-IS4QEuUku9zL0V3hFHt41fqmEdvphEvrOg0D4K_TgiiaePCDH2IPzt6B3O6NLHtgJNXk3UW68DL6KVBD4rjoW-nW9ACgmlkgnY0gmlwhDYnlGKJc2VjcDI1NmsxoQP3W66lisn2YOJF4B0QDEnURZ6tqzpi5gBy9rbMfpC70YN1ZHCCIyg",
        ],
    };
}
