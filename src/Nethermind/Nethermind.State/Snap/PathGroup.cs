// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

[assembly: InternalsVisibleTo("Nethermind.Network.Test")]

namespace Nethermind.State.Snap
{
    public class PathGroup
    {
        /// <summary>The longest compact-encoded path a trie node can sit at: 64 nibbles.</summary>
        public const int MaxPathLength = Nibbles.MaxNodePathNibbles / 2 + 1;

        /// <summary>
        /// Stands in for a decoded path longer than <see cref="MaxPathLength"/>, which is not copied. Being longer
        /// than that itself, it names no node and, as an account key, no account, whatever its bytes. Shared, so never
        /// written to.
        /// </summary>
        internal static readonly byte[] TooLongPath = new byte[MaxPathLength + 1];

        public byte[][] Group { get; set; } = [];

        public static RlpPathGroupList EncodeToRlpPathGroupList(IReadOnlyList<PathGroup> groups) =>
            new(EncodeToRlpItemList(groups));

        public static IRlpItemList EncodeToRlpItemList(IReadOnlyList<PathGroup> groups)
        {
            using DeferredRlpItemList.Builder builder = new();
            DeferredRlpItemList.Builder.Writer rootWriter = builder.BeginRootContainer();
            for (int i = 0; i < groups.Count; i++)
            {
                byte[][] paths = groups[i].Group;
                using DeferredRlpItemList.Builder.Writer groupWriter = rootWriter.BeginContainer();
                for (int j = 0; j < paths.Length; j++)
                    groupWriter.WriteValue(paths[j]);
            }
            rootWriter.Dispose();

            return builder.ToRlpItemList();
        }
    }
}
