// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Authentication;

public interface IRpcAuthentication
{
    bool Authenticate(string token);
}

public class NoAuthentication : IRpcAuthentication
{
    private NoAuthentication() { }

    public static NoAuthentication Instance = new();

    public bool Authenticate(string _) => true;
}
