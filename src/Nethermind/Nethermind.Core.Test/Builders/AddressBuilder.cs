// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public class AddressBuilder : BuilderBase<Address>
    {
        private static readonly ICryptoRandom CryptoRandom = new CryptoRandom();

        public AddressBuilder()
        {
            byte[] bytes = CryptoRandom.GenerateRandomBytes(20);
            TestObjectInternal = new Address(bytes);
        }

        public AddressBuilder FromNumber(int number)
        {
            TestObjectInternal = new Address(number.ToBigEndianByteArray().PadLeft(20));
            return this;
        }
    }
}
