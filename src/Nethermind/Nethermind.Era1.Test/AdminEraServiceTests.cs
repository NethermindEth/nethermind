// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Era1.Exceptions;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Nethermind.Era1.Test;

public class AdminEraServiceTests
{
    [Test]
    public void CanCallImport()
    {
        IEraImporter importer = Substitute.For<IEraImporter>();
        AdminEraService adminEraService = new(
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
        TaskCompletionSource importTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        importer.Import("somewhere", 99, 999, null).Returns(importTcs.Task);
        AdminEraService adminEraService = new(
            importer,
            Substitute.For<IEraExporter>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ImportHistory("somewhere", 99, 999, null);

        Assert.That(() => adminEraService.ImportHistory("somewhere", 99, 999, null), Throws.Exception);

        importTcs.TrySetResult();

        // Not throw — wait for fire-and-forget continuation to release the in-progress lock
        Assert.That(() => adminEraService.ImportHistory("somewhere", 99, 999, null),
            Throws.Nothing.After(5000, 50));
    }

    [Test]
    public void CanCallExport()
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        AdminEraService adminEraService = new(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ExportHistory("somewhere", 99, 999);
        exporter.Received().Export("somewhere", 99, 999);
    }

    [Test]
    public void ThrowsWhenExistingExportIsRunning([Values] bool exportFails)
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        TaskCompletionSource importTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        exporter.Export("somewhere", 99, 999).Returns(importTcs.Task);
        AdminEraService adminEraService = new(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        adminEraService.ExportHistory("somewhere", 99, 999);

        Assert.That(() => adminEraService.ExportHistory("somewhere", 99, 999), Throws.Exception);

        if (exportFails) importTcs.SetException(new EraException("Export failed"));
        else importTcs.TrySetResult();

        // Not throw — wait for fire-and-forget continuation to release the in-progress lock
        Assert.That(() => adminEraService.ExportHistory("somewhere", 0, 0),
            Throws.Nothing.After(5000, 50));
    }

    [Test]
    public void RejectedExportThrowsAndReleasesTheExportLock([Values] bool returnsFaultedTask)
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        ArgumentException rejection = new("Cannot export to a block after head block");
        if (returnsFaultedTask) exporter.Export("somewhere", 99, 999).Returns(Task.FromException(rejection));
        else exporter.Export("somewhere", 99, 999).Throws(rejection);
        AdminEraService adminEraService = new(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        Assert.That(() => adminEraService.ExportHistory("somewhere", 99, 999), Throws.ArgumentException);
        Assert.That(() => adminEraService.ExportHistory("somewhere", 0, 0), Throws.Nothing);
    }
}
