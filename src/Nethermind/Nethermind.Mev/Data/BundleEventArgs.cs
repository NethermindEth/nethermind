// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Mev.Data
{
    public class BundleEventArgs : EventArgs
    {
        public MevBundle MevBundle { get; }

        public BundleEventArgs(MevBundle mevBundle)
        {
            MevBundle = mevBundle;
        }
    }
}
