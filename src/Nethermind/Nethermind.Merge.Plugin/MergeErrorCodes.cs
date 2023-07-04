// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Contains the values of error codes defined in the Engine API
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/common.md">
/// Common Definitions</see>.
/// </summary>
public static class MergeErrorCodes
{
    /// <summary>
    /// Payload does not exist or is not available.
    /// </summary>
    public const int UnknownPayload = -38001;

    /// <summary>
    /// Forkchoice state is invalid or inconsistent.
    /// </summary>
    public const int InvalidForkchoiceState = -38002;

    /// <summary>
    /// Payload attributes are invalid or inconsistent.
    /// </summary>
    public const int InvalidPayloadAttributes = -38003;

    /// <summary>
    /// Number of requested entities is too large.
    /// </summary>
    public const int TooLargeRequest = -38004;
}
