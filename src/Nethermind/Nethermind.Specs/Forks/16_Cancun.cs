// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks
{
    public class Cancun : Shanghai
    {
        private static IReleaseSpec _instance;

        protected Cancun()
        {
            Name = "Cancun";
            IsEip1153Enabled = true;
            IsEip5656Enabled = true;
            IsEip4844Enabled = true;
            IsEip6780Enabled = true;
            IsEip4788Enabled = true;
            Eip4788ContractAddress = new Address("0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02");
        }

        public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Cancun());
    }
}
