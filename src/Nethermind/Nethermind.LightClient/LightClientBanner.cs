// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal static class LightClientBanner
{
    internal static string Render(string network, string rpcUrl, string checkpoint, bool redirected)
    {
        string blue = redirected ? "" : "\u001b[1;38;2;0;179;255m";
        string orange = redirected ? "" : "\u001b[1;38;2;255;153;0m";
        string reset = redirected ? "" : "\u001b[0m";
        string[] logo = [
            $"{blue}_____   __   ______  ___   {orange}______     _________",
            $"{blue}___  | / /   ___   |/  /   {orange}___  /     __  ____/",
            $"{blue}__   |/ /    __  /|_/ /    {orange}__  /      _  /",
            $"{blue}_  /|  /     _  /  / /     {orange}_  /___    / /___",
            $"{blue}/_/ |_/      /_/  /_/      {orange}/_____/    \\____/{reset}",
        ];
        string separator = redirected ? " | " : "  ·  ";
        return string.Join(Environment.NewLine, logo) + Environment.NewLine +
            $"    {network.ToUpperInvariant()}{separator}Beacon + execution P2P{separator}Local verification{Environment.NewLine}" +
            $"    RPC {rpcUrl}{separator}P2P :9050 / :30307{Environment.NewLine}" +
            $"    Trusted checkpoint {checkpoint}{Environment.NewLine}{Environment.NewLine}";
    }
}
