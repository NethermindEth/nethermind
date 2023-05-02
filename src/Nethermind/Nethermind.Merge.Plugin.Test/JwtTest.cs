// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class JwtTest
{
    [Test]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzV9.HfWy49SIyB12PBB_xEpy6IAiIan5mIqD6Jzeh_J1QNw", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Njd9.YGA0v88qMS7lp41wJQv9Msru6dwrNOHXHYiDsuhuScU", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzMsImlhdCI6MTY0NDk5NDk3MX0.ADc_b_tCac2uRHcNCekHvHV-qQ8hNyUjdxCVPETd3Os", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXIiOiJiYXoiLCJpYXQiOjE2NDQ5OTQ5NzF9.UZmoAYPGvKoWvz3KcXuxkDnVIF4Fn7QT7z9RwZgSREo", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTUwMzJ9.21Oqg-Q5Ug3HfxL0JmuOV_Caer7PtgwUhKkiLAQsBCI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5MTB9.o6H-ep8T3wI79OoCbs6QIK62BDoFMCg_jq75noyWbbI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMEe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF8.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.d88KZjmZ_nL0JTnsF6SR1BRBCjus4U3M-390HDDDNRc", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzEsImlhdCI6MTY0NDk5NDk3MX0.wU4z8ROPW-HaOgrUBG0FqTEutt7rWVsWMqXLvdEl_wI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTUwMzF9.fI6IzcITJKC5HJfsrnMc2kxKmi6kZEVcEjFcRzL6UGs", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NjF9.huhtaE1cUU2JuhqKmeTrHC3wgl2Tp_1pVh7DuYkKrQo", "true")]
    public void long_key_tests(string token, bool expected)
    {
        ManualTimestamper manualTimestamper = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(1644994971).UtcDateTime };
        IRpcAuthentication authentication = JwtAuthentication.FromSecret("5166546A576E5A7234753778214125442A472D4A614E645267556B5870327335", manualTimestamper, LimboTraceLogger.Instance);
        IRpcAuthentication authenticationWithPrefix = JwtAuthentication.FromSecret("0x5166546A576E5A7234753778214125442A472D4A614E645267556B5870327335", manualTimestamper, LimboTraceLogger.Instance);
        bool actual = authentication.Authenticate(token);
        Assert.That(actual, Is.EqualTo(expected));
        actual = authenticationWithPrefix.Authenticate(token);
        Assert.That(expected, Is.EqualTo(actual));
    }
}
