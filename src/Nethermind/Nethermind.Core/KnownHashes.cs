/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public static class KnownHashes
    {
        public static Keccak MainnetGenesis = new Keccak("0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3");
        public static Keccak MainnetDao = new Keccak("0x4985f5ca3d2afbec36529aa96f74de3cc10a2a4a6c44f2157a57d2c6059a11bb");
        public static Keccak MainnetConstantinopleFix = new Keccak("0xeddb0590e1095fbe51205a51a297daef7259e229af0432214ae6cb2c1f750750");
        
        public static Keccak GoerliGenesis = new Keccak("0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a");
        
        public static Keccak RinkebyGenesis = new Keccak("0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177");
        public static Keccak RinkebyConstantinopleFix = new Keccak("0xe2fa06d53b28bfa053e022686d6106026f8a1d5fe40e0eccd09e3f7165acd424");
        
        public static Keccak RopstenGenesis = new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d");
        public static Keccak RopstenConstantinopleFix = new Keccak("0x8696d2eed8197e186d8d682756a0c2a1947ab5e71257475ebcce4fa3252ee9f7");
        
        public static Keccak SokolGenesis = new Keccak("0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f");
    }
}