// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    public partial class StorageTree
    {
        /// <summary>Empty, so every slot key is hashed through <see cref="KeccakCache"/> instead.</summary>
        /// <remarks>
        /// The guest verifies one block, and the eager table of <c>StorageTree.std.cs</c> would hash a
        /// thousand slot indices to serve the few dozen distinct low slots that block touches — more
        /// keccak permutations than it saves. <see cref="KeccakCache"/> memoizes on the guest, so the
        /// repeats it does have cost a word compare rather than a hash.
        /// </remarks>
        private static readonly ValueHash256[] Lookup = [];
    }
}
