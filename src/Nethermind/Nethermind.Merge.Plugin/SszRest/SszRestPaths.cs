// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.SszRest;

public static class SszRestPaths
{
    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "client/version";

    public const string PayloadBodiesByHash = "payloads/bodies/by-hash";

    public const string PayloadBodiesByRange = "payloads/bodies/by-range";

    public const string Blobs = "blobs";
}
