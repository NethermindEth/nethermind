// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class SpuriousDragon : TangerineWhistle
    {
        private static IReleaseSpec _instance;

        protected SpuriousDragon()
        {
            Name = "Spurious Dragon";
            MaxCodeSize = 24576;
            IsEip155Enabled = true;
            IsEip158Enabled = true;
            IsEip160Enabled = true;
            IsEip170Enabled = true;
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new SpuriousDragon());
    }
}
