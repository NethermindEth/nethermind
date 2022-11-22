// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Crypto
{
    public class SigningRoot
    {
        public SigningRoot(Root objectRoot, Domain domain)
        {
            ObjectRoot = objectRoot;
            Domain = domain;
        }

        public Domain Domain { get; }
        public Root ObjectRoot { get; }
    }
}
