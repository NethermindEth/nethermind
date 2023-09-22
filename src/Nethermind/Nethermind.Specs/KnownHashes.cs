// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Specs
{
    public static class KnownHashes
    {
        public static readonly Keccak MainnetGenesis = new("0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3");
        public static readonly Keccak GoerliGenesis = new("0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a");
        public static readonly Keccak RinkebyGenesis = new("0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177");
        public static readonly Keccak SepoliaGenesis = new("0x25a5cc106eea7138acab33231d7160d69cb777ee0c2c553fcddf5138993e6dd9");
        public static readonly Keccak GnosisGenesis = new("0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756");
        public static readonly Keccak ChiadoGenesis = new("0xada44fd8d2ecab8b08f256af07ad3e777f17fb434f8f8e678b312f576212ba9a");
        public static readonly Keccak HoleskyGenesis = new("0xb5f7f912443c940f21fd611f12828d75b534364ed9e95ca4e307729a4661bde4");
    }
}
