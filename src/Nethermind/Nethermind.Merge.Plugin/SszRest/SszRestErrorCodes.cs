// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Internal sentinel error codes used by <see cref="Handlers.SszEndpointHandlerBase.WriteErrorAsync"/>
/// to produce the correct RFC 7807 <c>type</c> URI for SSZ-REST-specific error conditions
/// that share the same HTTP status as other, unrelated engine-API errors.
/// <para>
/// These are <em>not</em> exposed through the JSON-RPC layer.  They live in the range
/// <c>-39000</c> to <c>-39999</c> to avoid collision with the JSON-RPC standard codes
/// (<c>-32xxx</c>) and the engine-API extension codes (<c>-38xxx</c>).
/// </para>
/// </summary>
internal static class SszRestErrorCodes
{
    /// <summary>
    /// SSZ body could not be decoded at all (structural parse failure).
    /// </summary>
    public const int SszDecodeError = -39000;

    /// <summary>
    /// Request shape is wrong (missing or malformed query parameter, wrong field count, etc.).
    /// </summary>
    public const int InvalidRequest = -39001;

    /// <summary>
    /// URL does not match any registered endpoint.
    /// </summary>
    public const int MethodNotFound = -39002;

    /// <summary>
    /// <c>Content-Type</c> (POST) or <c>Accept</c> (GET) header is missing or wrong for an engine hot-path endpoint.
    /// </summary>
    public const int UnsupportedMediaType = -39003;

    /// <summary>
    /// SSZ body decoded successfully but contains semantically invalid values (e.g. a uint field that overflows its domain range).
    /// </summary>
    public const int InvalidBody = -39004;
}
