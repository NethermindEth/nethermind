// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ProductInfoTest
{
    [TearDown]
    public void TearDown()
    {
        ProductInfo.VersionPostfix = "";
        ProductInfo.InitializePublicClientId(ProductInfo.DefaultPublicClientIdFormat);
    }

    [Test]
    public void Public_client_id_template_properly_initialized()
    {
        ProductInfo.InitializePublicClientId("{name}/{version}/{os}/{runtime}");
        Assert.That(
            ProductInfo.PublicClientId, Is.EqualTo(
                $"{ProductInfo.Name}/v{ProductInfo.Version}/{ProductInfo.OS.ToLowerInvariant()}-{ProductInfo.OSArchitecture}/dotnet{ProductInfo.Runtime[5..]}"
            )
        );
    }

    [Test]
    public void Public_client_id_empty_when_template_is_empty()
    {
        ProductInfo.InitializePublicClientId("");
        Assert.That(ProductInfo.PublicClientId, Is.EqualTo(""));
    }

    [Test]
    public void Public_client_id_not_showing_os_and_runtime_when_hidden()
    {
        ProductInfo.InitializePublicClientId("{name}/{version}");
        Assert.That(ProductInfo.PublicClientId,
            Is.EqualTo($"{ProductInfo.Name}/v{ProductInfo.Version}"));
    }

    [Test]
    public void Public_client_id_not_initialized_returns_the_default_client_id() => Assert.That(ProductInfo.PublicClientId, Is.EqualTo(ProductInfo.ClientId));

    [TestCase("-halfpath", "Nethermind/v{0}-halfpath/{1}/dotnet{2}")]
    [TestCase("-hash", "Nethermind/v{0}-hash/{1}/dotnet{2}")]
    [TestCase("", "Nethermind/v{0}/{1}/dotnet{2}")]
    public void Version_postfix_appended_after_version(string postfix, string expectedTemplate)
    {
        ProductInfo.VersionPostfix = postfix;
        ProductInfo.InitializePublicClientId(ProductInfo.DefaultPublicClientIdFormat);

        string os = $"{ProductInfo.OS.ToLowerInvariant()}-{ProductInfo.OSArchitecture}";
        string expected = string.Format(expectedTemplate, ProductInfo.Version, os, ProductInfo.Runtime[5..]);
        Assert.That(ProductInfo.PublicClientId, Is.EqualTo(expected));
    }
}
