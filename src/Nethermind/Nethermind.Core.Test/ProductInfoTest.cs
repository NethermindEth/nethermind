// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
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
    public void Public_client_id_includes_version_postfix([Values("-hp", "-f", "")] string postfix)
    {
        ProductInfo.VersionPostfix = postfix;
        ProductInfo.InitializePublicClientId(ProductInfo.DefaultPublicClientIdFormat);
        Assert.That(
            ProductInfo.PublicClientId, Is.EqualTo(
                $"{ProductInfo.Name}/v{ProductInfo.Version}{postfix}/{ProductInfo.OS.ToLowerInvariant()}-{ProductInfo.OSArchitecture}/dotnet{ProductInfo.Runtime[5..]}"
            )
        );
    }

    [Test]
    public void Public_client_id_not_initialized_returns_the_default_client_id() => Assert.That(ProductInfo.PublicClientId, Is.EqualTo(ProductInfo.ClientId));

    [TestCase("1.2.3", "1.2.3", "")]
    [TestCase("1.2.3+", "1.2.3", "")]
    [TestCase("1.2.3+a", "1.2.3+a", "a")]
    [TestCase("1.2.3+abcdefg", "1.2.3+abcdefg", "abcdefg")]
    [TestCase("1.2.3+abcdefgh", "1.2.3+abcdefgh", "abcdefgh")]
    [TestCase("1.2.3+abcdefghi", "1.2.3+abcdefgh", "abcdefghi")]
    [TestCase("1.2.3+abc+def", "1.2.3+abc+def", "abc+def")]
    [TestCase("1.2.3-rc+0123456789abcdef0123456789abcdef01234567", "1.2.3-rc+01234567", "0123456789abcdef0123456789abcdef01234567")]
    public void Split_keeps_up_to_eight_commit_characters_in_the_version(string informationalVersion, string version, string commit) =>
        Assert.That(ProductInfo.SplitInformationalVersion(informationalVersion), Is.EqualTo((version, commit)));

    [Test]
    public void Split_keeps_the_previous_result_for_empty_and_long_suffixes()
    {
        const string distinct = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ._";
        using (Assert.EnterMultipleScope())
        {
            foreach (int length in Enumerable.Range(9, distinct.Length - 8).Prepend(0))
            {
                string informationalVersion = $"2.3.0-preview+{distinct[..length]}";
                int index = informationalVersion.IndexOf('+');
                (string, string) previous = (informationalVersion[..Math.Min(index + 9, informationalVersion.Length - 1)], informationalVersion[(index + 1)..]);
                Assert.That(ProductInfo.SplitInformationalVersion(informationalVersion), Is.EqualTo(previous), $"suffix of {length} characters");
            }
        }
    }

    [Test]
    public void Version_and_commit_are_the_split_of_the_entry_assembly_version()
    {
        string informationalVersion = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.That((ProductInfo.Version, ProductInfo.Commit), Is.EqualTo(ProductInfo.SplitInformationalVersion(informationalVersion)));
    }
}
