// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Baseline.Tree
{
    public class BaselineMetadata
    {
        public BaselineMetadata(params Address[] trackedTrees)
        {
            TrackedTrees = trackedTrees;
        }

        public Address[] TrackedTrees { get; set; }
    }
}
