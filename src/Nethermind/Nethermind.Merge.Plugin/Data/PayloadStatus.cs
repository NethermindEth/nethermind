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

        /// <summary>
        /// EIP-7805 (FOCIL): the block executed cleanly but the inclusion-list constraint was
        /// not satisfied — an appendable IL transaction was omitted from the payload.
        /// </summary>
        public const string InclusionListUnsatisfied = "INCLUSION_LIST_UNSATISFIED";

        /// <summary>
        /// EIP-7805 (FOCIL): the block is accepted but its inclusion-list compliance could not be
        /// derived. Internal only — reported on the wire as <see cref="Valid"/> with a null
        /// <c>inclusionListSatisfied</c>, since claiming compliance that was never checked would be wrong.
        /// </summary>
        public const string InclusionListNotEvaluated = "INCLUSION_LIST_NOT_EVALUATED";
    }
}
