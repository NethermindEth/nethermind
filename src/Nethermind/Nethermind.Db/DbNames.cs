// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public static class DbNames
    {
        public const string Storage = "storage";
        public const string State = "state";
        public const string Code = "code";
        public const string Blocks = "blocks";
        public const string Headers = "headers";
        public const string Receipts = "receipts";
        public const string BlockInfos = "blockInfos";
        public const string Bloom = "bloom";
        public const string Witness = "witness";
        public const string CHT = "canonicalHashTrie";
        public const string Metadata = "metadata";
        public const string Leaf = "leaf";
        public const string InternalNodes = "internalNodes";
        public const string ForwardDiff = "forwardDiff";
        public const string ReverseDiff = "reverseDiff";
        public const string Preimages = "preimages";
        public const string StateRootToBlock = "stateRoots";
    }
}
