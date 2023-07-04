// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core
{
    public class AddressComparer : IComparer<Address>
    {
        private AddressComparer()
        {
        }

        public static AddressComparer Instance { get; } = new();

        public int Compare(Address? x, Address? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }

            if (y is null)
            {
                return 1;
            }

            for (int i = 0; i < Address.ByteLength; i++)
            {
                if (x.Bytes[i] < y.Bytes[i])
                {
                    return -1;
                }

                if (x.Bytes[i] > y.Bytes[i])
                {
                    return 1;
                }
            }


            return 0;
        }
    }
}
