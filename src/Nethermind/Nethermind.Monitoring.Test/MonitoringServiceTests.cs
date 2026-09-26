// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http.Headers;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test;

public class MonitoringServiceTests
{
    [TestCase("user", "pass")]
    [TestCase("user", "p@ss:word")]
    public void Basic_auth_header_encodes_credentials(string username, string password)
    {
        AuthenticationHeaderValue header = MonitoringService.CreateBasicAuthHeader(username, password);

        Assert.That(header.Scheme, Is.EqualTo("Basic"));
        Assert.That(header.Parameter, Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))));
    }
}
