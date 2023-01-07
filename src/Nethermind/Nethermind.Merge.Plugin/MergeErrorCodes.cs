// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin
{
    // Error codes spec: https://github.com/ethereum/execution-apis/blob/v1.0.0-alpha.5/src/engine/specification.md#errors
    public static class MergeErrorCodes
    {
        public const int None = 0;

        public const int UnknownPayload = -38001;

        public const int InvalidForkchoiceState = -38002;

        public const int InvalidPayloadAttributes = -38003;
    }
}
