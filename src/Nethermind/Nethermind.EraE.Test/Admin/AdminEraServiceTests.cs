// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.EraE.Admin;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Admin;

public class AdminEraServiceTests
{
    [Test]
    public void ImportHistory_WhenCalled_DelegatesToImporter()
    {
        IEraImporter importer = Substitute.For<IEraImporter>();
        AdminEraService sut = new(
            importer,
            Substitute.For<IEraExporter>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        sut.ImportHistory("somewhere", 99, 999, null);

        importer.Received().Import("somewhere", 99, 999, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public void ImportHistory_WhenImportAlreadyRunning_ReturnsFailure()
    {
        IEraImporter importer = Substitute.For<IEraImporter>();
        TaskCompletionSource tcs = new();
        importer.Import("somewhere", 99, 999, null, Arg.Any<CancellationToken>()).Returns(tcs.Task);

        AdminEraService sut = new(
            importer,
            Substitute.For<IEraExporter>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        sut.ImportHistory("somewhere", 99, 999, null);

        ResultWrapper<string> result = sut.ImportHistory("somewhere", 99, 999, null);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));

        tcs.TrySetResult();

        Assert.That(() => sut.ImportHistory("somewhere", 99, 999, null), Throws.Nothing);
    }

    [Test]
    public void ExportHistory_WhenCalled_DelegatesToExporter()
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        AdminEraService sut = new(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        sut.ExportHistory("somewhere", 99, 999);

        exporter.Received().Export("somewhere", 99, 999, Arg.Any<CancellationToken>());
    }

    [Test]
    public void ExportHistory_WhenExportAlreadyRunning_ReturnsFailure()
    {
        IEraExporter exporter = Substitute.For<IEraExporter>();
        TaskCompletionSource tcs = new();
        exporter.Export("somewhere", 99, 999, Arg.Any<CancellationToken>()).Returns(tcs.Task);

        AdminEraService sut = new(
            Substitute.For<IEraImporter>(),
            exporter,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);

        sut.ExportHistory("somewhere", 99, 999);

        ResultWrapper<string> result = sut.ExportHistory("somewhere", 99, 999);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));

        tcs.TrySetResult();

        Assert.That(() => sut.ExportHistory("somewhere", 99, 999), Throws.Nothing);
    }
}
