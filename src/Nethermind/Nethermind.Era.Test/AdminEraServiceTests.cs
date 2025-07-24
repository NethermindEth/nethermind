// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.Era1.Test;

public class AdminEraServiceTests
{
    [Test]
    public void CanCallcExport()
    {
        IEraImporter importer = Substitute.For<IEraImporter>();
        AdminEraService adminEraService = new AdminEraService(
            importer,
            Substitute.For<IEraExporter>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ImportHistory("somewhere", 99, 999, null);
        importer.Received().Import("somewhere", 99, 999, null);
    }

    [Test]
    public void ThrowsWhenExistingImportIsRunning()
    {
        IEraImporter importer = Substitute.For<IEraImporter>();
        TaskCompletionSource importTcs = new TaskCompletionSource();
        importer.Import("somewhere", 99, 999, null).Returns(importTcs.Task);
        AdminEraService adminEraService = new AdminEraService(
            importer,
            Substitute.For<IEraExporter>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ImportHistory("somewhere", 99, 999, null);

        Assert.That(() => adminEraService.ImportHistory("somewhere", 99, 999, null), Throws.Exception);

        importTcs.TrySetResult();

        // Not throw
        adminEraService.ImportHistory("somewhere", 99, 999, null);
    }

    [Test]
    public void CanCallExport()
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        AdminEraService adminEraService = new AdminEraService(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ExportHistory("somewhere", 99, 999);
        exporter.Received().Export("somewhere", 99, 999);
    }

    [Test]
    public void ThrowsWhenExistingExportIsRunning()
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        TaskCompletionSource importTcs = new TaskCompletionSource();
        exporter.Export("somewhere", 99, 999).Returns(importTcs.Task);
        AdminEraService adminEraService = new AdminEraService(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ExportHistory("somewhere", 99, 999);

        Assert.That(() => adminEraService.ExportHistory("somewhere", 99, 999), Throws.Exception);

        importTcs.TrySetResult();

        // Not throw
        adminEraService.ExportHistory("somewhere", 99, 999);
    }
}
