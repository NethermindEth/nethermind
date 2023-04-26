// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Snarks;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class BnMulPrecompileTests
    {
        [Test]
        public void Test()
        {
            byte[][] inputs =
            {
                Bytes.FromHexString("089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("23f16f1bcc31bd002746da6fa3825209af9a356ccd99cf79604a430dd592bcd90a03caeda9c5aa40cdc9e4166e083492885dad36c72714e3697e34a4bc72ccaaffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("21315394462f1a39f87462dbceb92718b220e4f80af516f727ad85380fadefbc2e4f40ea7bbe2d4d71f13c84fd2ae24a4a24d9638dd78349d0dee8435a67cca6ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("0341b65d1b32805aedf29c4704ae125b98bb9b736d6e05bd934320632bf46bb60d22bc985718acbcf51e3740c1565f66ff890dfd2302fc51abc999c83d8774baffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("08ed1b33fe3cd3b1ac11571999e8f451f5bb28dd4019e58b8d24d91cf73dc38f11be2878bb118612a7627f022aa19a17b6eb599bba4185df357f81d052fff90bffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("279e2a1eee50ae1e3fe441dcd58475c40992735644de5c8f6299b6f0c1fe41af21b37bd13a881181d56752e31cf494003a9d396eb908452718469bc5c75aa807ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("1c35e297f7c55363cd2fd00d916c67fad3bdea15487bdc5cc7b720f3a2c8b776106c2a4cf61ab73f91f2258f1846b9be9d28b9a7e83503fa4f4b322bfc07223cffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("0af6f1fd0b29a4f055c91a472f285e919d430a2b73912ae659224e24a458c65e2c1a52f5abf3e86410b9a603159b0bf51abf4d72cbd5e8161a7b5c47d60dfe57ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Bytes.FromHexString("1f752f85cf5cc01b2dfe279541032da61c2fcc8ae0dfc6d4253ba9b5d3c858231d03a84afe2a9f595ab03007400ccd36a2c0bc31203d881011dfc450c39b5abeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            };

            for (int i = 0; i < inputs.Length; i++)
            {
                IPrecompile precompile = Bn254MulPrecompile.Instance;
                _ = precompile.Run(inputs[i], MuirGlacier.Instance);
            }
        }
    }
}
