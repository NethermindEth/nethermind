// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data
{
    public static class PayloadStatus
    {
        /// <summary>
        /// Payload is valid.
        /// </summary>
        public const string Valid = "VALID";

        /// <summary>
        /// Payload is invalid.
        /// </summary>
        public const string Invalid = "INVALID";

        /// <summary>
        /// Payload started a sync.
        /// </summary>
        public const string Syncing = "SYNCING";

        /// <summary>
        /// Payload was accepted but not executed yet. It can be executed in <see cref="ForkchoiceStateV1"/> call.
        /// </summary>
        public const string Accepted = "ACCEPTED";

        /// <summary>The block executed cleanly but omitted an appendable inclusion-list transaction (EIP-7805).</summary>
        public const string InclusionListUnsatisfied = "INCLUSION_LIST_UNSATISFIED";

        /// <summary>The block is accepted but its inclusion-list compliance could not be derived.</summary>
        /// <remarks>Internal only: on the wire this is <see cref="Valid"/> with a null <c>inclusionListSatisfied</c>,
        /// since claiming compliance that was never checked would be wrong.</remarks>
        public const string InclusionListNotEvaluated = "INCLUSION_LIST_NOT_EVALUATED";
    }
}
