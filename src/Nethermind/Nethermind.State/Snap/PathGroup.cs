// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class PathGroup
    {
        public byte[][] Group { get; set; }

        public static RlpPathGroupList EncodeToRlpPathGroupList(IReadOnlyList<PathGroup> groups) =>
            new(EncodeToRlpItemList(groups));

        public static IRlpItemList EncodeToRlpItemList(IReadOnlyList<PathGroup> groups)
        {
            using RlpItemList.Builder builder = new();
            RlpItemList.Builder.Writer rootWriter = builder.BeginRootContainer();
            for (int i = 0; i < groups.Count; i++)
            {
                byte[][] paths = groups[i].Group;
                using RlpItemList.Builder.Writer groupWriter = rootWriter.BeginContainer();
                for (int j = 0; j < paths.Length; j++)
                    groupWriter.WriteValue(paths[j]);
            }
            rootWriter.Dispose();

            return builder.ToRlpItemList();
        }
    }
}
