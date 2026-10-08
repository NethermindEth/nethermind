// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal static class LightClientBanner
{
    internal static string Render(string network, string rpcUrl, string checkpoint, bool redirected)
    {
        string[] logo = redirected
            ? [
                "          *",
                "         / \\",
                "        *---*       N E T H E R M I N D",
                "         \\ /        L I G H T   C L I E N T",
                "          *",
                "    -------------------------------------------",
            ]
            : [
                "          ◆",
                "         ╱ ╲",
                "        ◆ ─ ◆       N E T H E R M I N D",
                "         ╲ ╱        L I G H T   C L I E N T",
                "          ◆",
                "    ───────────────────────────────────────────",
            ];
        string separator = redirected ? " | " : "  ·  ";
        return string.Join(Environment.NewLine, logo) + Environment.NewLine +
            $"    {network.ToUpperInvariant()}{separator}Beacon + execution P2P{separator}Local verification{Environment.NewLine}" +
            $"    RPC {rpcUrl}{separator}P2P :9050 / :30307{Environment.NewLine}" +
            $"    Trusted checkpoint {checkpoint}{Environment.NewLine}{Environment.NewLine}";
    }
}
