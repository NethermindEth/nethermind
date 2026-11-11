// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Runtime.Loader;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ProductInfoTest
{
    [Test, NonParallelizable]
    public void Metadata_initializes_with_or_without_managed_entry_assembly([Values] bool managedEntry)
    {
        Assembly? entry = Assembly.GetEntryAssembly();
        AssemblyLoadContext context = new(nameof(ProductInfoTest), isCollectible: true);
        try
        {
            Assembly core = context.LoadFromAssemblyPath(typeof(ProductInfo).Assembly.Location);
            Assembly.SetEntryAssembly(managedEntry ? typeof(ProductInfoTest).Assembly : null);
            Type product = core.GetType(typeof(ProductInfo).FullName!, throwOnError: true)!;
            string expectedName = (managedEntry ? typeof(ProductInfoTest).Assembly : core)
                .GetCustomAttribute<AssemblyProductAttribute>()!.Product;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(product.GetProperty(nameof(ProductInfo.Name))!.GetValue(null), Is.EqualTo(expectedName));
                Assert.That(product.GetProperty(nameof(ProductInfo.ClientId))!.GetValue(null),
                    Does.StartWith(expectedName + "/v"));
            }
        }
        finally
        {
            Assembly.SetEntryAssembly(entry);
            context.Unload();
        }
    }

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
}
