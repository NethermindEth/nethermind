// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.SecretProvider;
using Nethermind.Tools.Kute.SystemClock;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class AuthTests
{
    [Test]
    public void CreateValidJWT()
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        var secretProvider = Substitute.For<ISecretProvider>();
        secretProvider.Secret.Returns("11ade2f6d95da8d71515d8c446d2a9cfbaefd1de40d78ccbea5c49315dc30237");

        var auth = new JwtAuth(clock, secretProvider);
        string token = auth.AuthToken;

        token.Should().BeEquivalentTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjB9.tWzIC8uadmVRHxZrv1TK57PyW95hmGrS0PgsV7FiFvw");
    }

    [Test]
    public void RefreshAuthAfterTTL()
    {
        var clock = Substitute.For<ISystemClock>();
        var secretProvider = Substitute.For<ISecretProvider>();
        secretProvider.Secret.Returns("11ade2f6d95da8d71515d8c446d2a9cfbaefd1de40d78ccbea5c49315dc30237");

        var initialTime = DateTimeOffset.UnixEpoch;
        var ttl = TimeSpan.FromSeconds(10);

        var auth = new TtlAuth(new JwtAuth(clock, secretProvider), clock, ttl);

        // Initial time
        {
            clock.UtcNow.Returns(initialTime);
            string token = auth.AuthToken;
            token.Should().BeEquivalentTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjB9.tWzIC8uadmVRHxZrv1TK57PyW95hmGrS0PgsV7FiFvw");
        }

        // Before TTL
        {
            clock.UtcNow.Returns(initialTime.Add(ttl.Subtract(TimeSpan.FromSeconds(1))));
            string token = auth.AuthToken;
            token.Should().BeEquivalentTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjB9.tWzIC8uadmVRHxZrv1TK57PyW95hmGrS0PgsV7FiFvw");
        }

        // After TTL, first iteration
        {
            clock.UtcNow.Returns(initialTime.Add(ttl));
            string token = auth.AuthToken;
            token.Should().BeEquivalentTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjEwfQ.XjVeTLNz9GnJiBuQMbcsDJyHOW3saZDmYH4MsyUhXBw");
        }

        // After TTL, second iteration
        {
            clock.UtcNow.Returns(initialTime.Add(ttl).Add(ttl));
            string token = auth.AuthToken;
            token.Should().BeEquivalentTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjIwfQ.NcHVs9-I1hgy0D664ruwgW4L1IrNT6fM2NZ45oQXbfY");
        }
    }
}
