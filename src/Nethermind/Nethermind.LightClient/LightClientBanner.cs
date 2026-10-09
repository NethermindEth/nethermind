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
            $"     {blue}_____   ____  ___{orange}  __    ______{reset}",
            $"     {blue}___  | / /  |/  /{orange} / /   / ____/{reset}",
            $"     {blue}__   |/ / /|_/ /{orange} / /   / /{reset}",
            $"     {blue}_  /|  / /  / /{orange} / /___/ /___{reset}",
            $"     {blue}/_/ |_/_/  /_/{orange} /_____/\\____/{reset}",
        ];
        const string border = "--------------------------------------------------------------------";
        string separator = redirected ? " | " : "  ·  ";
        return Environment.NewLine + string.Join(Environment.NewLine, logo) +
            Environment.NewLine + Environment.NewLine +
            $"{network.ToUpperInvariant()}{separator}Beacon + execution P2P{separator}Local verification{Environment.NewLine}" +
            $"RPC {rpcUrl}{separator}P2P :9050 / :30307{Environment.NewLine}" +
            $"Trusted checkpoint {checkpoint}{Environment.NewLine}" +
            border + Environment.NewLine + Environment.NewLine;
    }
}
