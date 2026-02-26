// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class PathGroup
    {
        public byte[][] Group { get; set; }

        public static RlpItemList EncodeToRlpItemList(IReadOnlyList<PathGroup> groups)
        {
            using RlpItemList.Builder builder = new();
            for (int i = 0; i < groups.Count; i++)
            {
                byte[][] paths = groups[i].Group;
                using RlpItemList.Builder.Writer groupWriter = builder.BeginContainer();
                for (int j = 0; j < paths.Length; j++)
                    groupWriter.WriteValue(paths[j]);
            }

            return builder.ToRlpItemList();
        }
    }
}
