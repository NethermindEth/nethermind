// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Core.Authentication;

public interface IRpcAuthentication
{
    Task<bool> Authenticate(string token);
}

public class NoAuthentication : IRpcAuthentication
{
    private NoAuthentication() { }

    public static NoAuthentication Instance = new();

    public Task<bool> Authenticate(string _) => Task.FromResult(true);
}
