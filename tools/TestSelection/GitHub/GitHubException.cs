// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.TestSelection.GitHub;

public sealed class GitHubException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status => status;
}
