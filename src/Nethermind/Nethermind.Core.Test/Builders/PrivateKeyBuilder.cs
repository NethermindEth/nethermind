// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public class PrivateKeyBuilder : BuilderBase<PrivateKey>
    {
        private PrivateKeyGenerator _generator = new();

        public PrivateKeyBuilder()
        {
            TestObject = _generator.Generate();
        }
    }
}
