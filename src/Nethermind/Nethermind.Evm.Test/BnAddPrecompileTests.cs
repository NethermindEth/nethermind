//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class BnAddPrecompileTests
    {
        [Test]
        public void Test()
        {
            byte[][] inputs =
            {
                Bytes.FromHexString("089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b3625f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf"),
                Bytes.FromHexString("23f16f1bcc31bd002746da6fa3825209af9a356ccd99cf79604a430dd592bcd90a03caeda9c5aa40cdc9e4166e083492885dad36c72714e3697e34a4bc72ccaa21315394462f1a39f87462dbceb92718b220e4f80af516f727ad85380fadefbc2e4f40ea7bbe2d4d71f13c84fd2ae24a4a24d9638dd78349d0dee8435a67cca6"),
                Bytes.FromHexString("0341b65d1b32805aedf29c4704ae125b98bb9b736d6e05bd934320632bf46bb60d22bc985718acbcf51e3740c1565f66ff890dfd2302fc51abc999c83d8774ba08ed1b33fe3cd3b1ac11571999e8f451f5bb28dd4019e58b8d24d91cf73dc38f11be2878bb118612a7627f022aa19a17b6eb599bba4185df357f81d052fff90b"),
                Bytes.FromHexString("279e2a1eee50ae1e3fe441dcd58475c40992735644de5c8f6299b6f0c1fe41af21b37bd13a881181d56752e31cf494003a9d396eb908452718469bc5c75aa8071c35e297f7c55363cd2fd00d916c67fad3bdea15487bdc5cc7b720f3a2c8b776106c2a4cf61ab73f91f2258f1846b9be9d28b9a7e83503fa4f4b322bfc07223c"),
                Bytes.FromHexString("0af6f1fd0b29a4f055c91a472f285e919d430a2b73912ae659224e24a458c65e2c1a52f5abf3e86410b9a603159b0bf51abf4d72cbd5e8161a7b5c47d60dfe571f752f85cf5cc01b2dfe279541032da61c2fcc8ae0dfc6d4253ba9b5d3c858231d03a84afe2a9f595ab03007400ccd36a2c0bc31203d881011dfc450c39b5abe"),
                Bytes.FromHexString("0e6eab4103302750b22364bd1ec80e5edfb3ad06fa175ff2517ca49489f728e9050a17b5a594d0fd6fafed7fe5c447793fe9b617f0f97c3ee6dd29638f6c9232038de98419e242685862c118253ab7df7358f863a59170c37e606d5bd23c742f076ff3443f4e01b7d7ace1315fe50cf77c365d8d289c65303bcc11ba7961ab95"),
                Bytes.FromHexString("1920c53c756f1ec1a40e0264e5f65808eafaeaa7b0885f89852297bc2186ac9d09416cc536a27b6d5616f74dd2bbbfb463b9961752e0aa38d47b5213994959ab015296293a5a1bb5e15a7d019787422cb3409e075e122c6fc5867f0c3f3715731782b870b6641d8d55323e27ebaea17909499877fda62e3ac1e2b2310cad5f9c"),
                Bytes.FromHexString("001faaf97b965ffa633612b7c8f9f4be0b286b19662e5cbe6878019d8ba1382b16567ced7a7ee5c272bbc378a95c2436fb0c6133649c77e55a708b28419b5cac0750d51706ced69621c8e4ba1758ba90c39ba8b3b50507bfa545ace1737e360e283d609cd67a291fc3d720c5b1113eececba4ca31d58a1319d6a5a2fa89608f9"),
                Bytes.FromHexString("128b65cb80257f3006fc20dbb6af6781da7e0f9213d2b909fd113ee0f2d2bb52251e288387db7be742fe67261f36a4f09eeb4763bbbaa1bb13af3dec65302a4115f64edf27478045bf45eded285544acaa7f2b3a2a36176acefc1a3d7181a73219d4344489688c2a2f16caf1141bc42021738339431b3a64cfbc293a73c1eddc"),
                Bytes.FromHexString("16a9fe4620e58d70109d6995fe5f9eb8b3d533280cc604a333dcf0fa688b62e20b972bf2daef6c10a41db685c2417b6f4362032421c8466277d3271b6e8706a809ad61a8a83df55f6cd293cd674338c35dbb32722e9db2d1a3371b43496c05fa09c73b138499e36453d67a2c9b543c2188918287c4eef2c3ccc9ebe1d6142d01")
            };

            for (int i = 0; i < inputs.Length; i++)
            {
                IPrecompile shamatar = Precompiles.Snarks.Shamatar.Bn256AddPrecompile.Instance;
                (ReadOnlyMemory<byte>, bool) resultShamatar = shamatar.Run(inputs[i], MuirGlacier.Instance);
            }
        }
    }
}
