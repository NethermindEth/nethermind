// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    public class PublicKeyConverterTests : ConverterTestBase<PublicKey>
    {
        [Test]
        public void Null_handling()
        {
            TestConverter(null!, (key, publicKey) => key == publicKey, new PublicKeyConverter());
        }

        [Test]
        public void Zero_handling()
        {
            TestConverter(new PublicKey(new byte[64]), (key, publicKey) => key == publicKey, new PublicKeyConverter());
        }
    }
}
