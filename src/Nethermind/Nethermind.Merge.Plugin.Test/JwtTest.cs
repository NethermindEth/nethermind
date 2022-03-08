//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Authentication;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class JwtTest
{
    [Test]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzV9.QRtFFE5NnbK_mMu-3qtPGPiAgTRCvb-Z1Ti_uwBjgDk", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Njd9.lJP7Nw_Lio-gP78ZW-Uv3PVdLbuaIMVgU9uvLw1V1BY", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzMsImlhdCI6MTY0NDk5NDk3MX0.1RVPaAjpjQWFqm33C87zdUThUbob96C5SHBVn_LDLDc", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXIiOiJiYXoiLCJpYXQiOjE2NDQ5OTQ5NzF9.EU7c1vsCWHU9fCV888yf1IwJR7uczhk5pKCB6CAd_NI", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Nzd9.r_MM-6TLGUtsf_EalbJKxgO-Vw6LOkTEqKjcEBSCRHw", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NjV9.sWMMjsne2hK0S20OL3lP_qVvnGIGvBc5fa7sUvJUiqM", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzUxMiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzV9.Av2ZI-xeXA8-VuSoYxCsnn0cCg_4St2zOSgFKbvsS1ObTZKLeltSV4CcTcraukYL_HNun3rI4iDjDxs6EJgbCA", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzEsImlhdCI6MTY0NDk5NDk3MX0.Nc6fT-W8bknDUqnjEwHKLreTguYgzMBlbsPAMO2OOHM", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.t-IDcSemACt8x4iTMCda8Yhe3iZaWbvV5XKSTbuAn0M", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.tICF9zHKdMOwccLLA2LGqbA_P1X8WHD-KMe5R4GpgkE", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.JxoxCpDIzhNLqBCvSWJjddHQ87SynxgwTjJP0-PapA4", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.JxoxCpDIzhNLqBCvSWJjddHQ87SynxgwTjJP0-PapA4", "false")]
    [TestCase("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("Bearer  eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("Bearer: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("Bearer:eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("Bearer\teyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    [TestCase("Bearer \teyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.3MaCM_vL7Dl50v0FMEJeVWwYckxifqxGtA2dlZA9YHQ", "false")]
    public void geth_tests(string token, bool expected)
    {
        // Only for JwtAuthentication class
        var mock = Substitute.For<IClock>();
        mock.GetCurrentTime().Returns(1644994971);
        IRpcAuthentication authentication = JwtAuthentication.FromHexSecret("736563726574", mock);
        IRpcAuthentication authenticationWithPrefix = JwtAuthentication.FromHexSecret("0x736563726574", mock);
        bool actual = authentication.Authenticate(token);
        Assert.AreEqual(expected, actual);
        actual = authenticationWithPrefix.Authenticate(token);
        Assert.AreEqual(actual, expected);
    }
    
    [Test]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzV9.HfWy49SIyB12PBB_xEpy6IAiIan5mIqD6Jzeh_J1QNw", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Njd9.YGA0v88qMS7lp41wJQv9Msru6dwrNOHXHYiDsuhuScU", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzMsImlhdCI6MTY0NDk5NDk3MX0.ADc_b_tCac2uRHcNCekHvHV-qQ8hNyUjdxCVPETd3Os", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXIiOiJiYXoiLCJpYXQiOjE2NDQ5OTQ5NzF9.UZmoAYPGvKoWvz3KcXuxkDnVIF4Fn7QT7z9RwZgSREo", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Nzd9.QydUOgQDbnaM66i5-YKWFqmQFV_vqO2-wHCR0GbyUz8", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NjV9.PvVSCk5oBSgJ77JNUw_PM9kak-1aM9VJD1qvTNIpFVw", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMEe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF8.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.d88KZjmZ_nL0JTnsF6SR1BRBCjus4U3M-390HDDDNRc", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzEsImlhdCI6MTY0NDk5NDk3MX0.wU4z8ROPW-HaOgrUBG0FqTEutt7rWVsWMqXLvdEl_wI", "false")]
    public void long_key_tests(string token, bool expected)
    {
        var mock = Substitute.For<IClock>();
        mock.GetCurrentTime().Returns(1644994971);
        IRpcAuthentication authentication = MicrosoftJwtAuthentication.CreateFromHexSecret("5166546A576E5A7234753778214125442A472D4A614E645267556B5870327335", mock);
        IRpcAuthentication authenticationWithPrefix = MicrosoftJwtAuthentication.CreateFromHexSecret("0x5166546A576E5A7234753778214125442A472D4A614E645267556B5870327335", mock);
        bool actual = authentication.Authenticate(token);
        Assert.AreEqual(expected, actual);
        actual = authenticationWithPrefix.Authenticate(token);
        Assert.AreEqual(actual, expected);
    }
}
