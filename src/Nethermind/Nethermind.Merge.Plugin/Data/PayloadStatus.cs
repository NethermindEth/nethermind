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
        /// <remarks>
        /// Wire value mandated by <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>.
        /// The C# member is named <see cref="InvalidInclusionList"/> to match the existing
        /// nethermind convention (one-camel-cased word per status), but the string sent on
        /// the wire must be the spec's <c>INCLUSION_LIST_UNSATISFIED</c>.
        /// </remarks>
        public const string InvalidInclusionList = "INCLUSION_LIST_UNSATISFIED";
    }
}
