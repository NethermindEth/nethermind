// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ProductInfoTest
{
    [Test]
    public void Client_id_preserved_when_no_hidden_parts()
    {
        Assert.That(
            ProductInfo.FormatClientId() ==
            $"{ProductInfo.Name}/v{ProductInfo.Version}/{ProductInfo.OS.ToLowerInvariant()}-{ProductInfo.OSArchitecture}/dotnet{ProductInfo.Runtime[5..]}"
        );
    }

    [Test]
    public void Client_id_preserved_when_part_names_are_incorrect()
    {
        Assert.That(
            ProductInfo.FormatClientId("osarch,runtim,$#") ==
            $"{ProductInfo.Name}/v{ProductInfo.Version}/{ProductInfo.OS.ToLowerInvariant()}-{ProductInfo.OSArchitecture}/dotnet{ProductInfo.Runtime[5..]}"
        );
    }

    [Test]
    public void Client_id_preserved_when_duplicates_in_hidden_parts()
    {
        Assert.That(
            ProductInfo.FormatClientId("os, os") ==
            $"{ProductInfo.Name}/v{ProductInfo.Version}/dotnet{ProductInfo.Runtime[5..]}"
        );
    }

    [Test]
    public void Client_id_empty_when_everything_is_hidden()
    {
        Assert.That(
            ProductInfo.FormatClientId("runtime,os,name,version") == ""
        );
    }

    [Test]
    public void Client_id_not_showing_os_and_runtime_when_hidden()
    {
        Assert.That(
            ProductInfo.FormatClientId("runtime,os") ==
            $"{ProductInfo.Name}/v{ProductInfo.Version}"
        );
    }
}
