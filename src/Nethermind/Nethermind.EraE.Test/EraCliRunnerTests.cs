// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Config;
using EraException = Nethermind.Era1.EraException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.History;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EraE.Test;

public class EraCliRunnerTests
{
    [Test]
    public void Run_WithImportDirectory_CallsEraImporter()
    {
        IEraImporter eraImporter = Substitute.For<IEraImporter>();
        IEraEConfig eraConfig = new EraEConfig
        {
            ImportDirectory = "import dir",
            From = 99,
            To = 999
        };
        EraCliRunner cliRunner = new(eraConfig, new HistoryConfig(), eraImporter, Substitute.For<IEraExporter>());

        _ = cliRunner.Run(default);

        eraImporter.Received().Import("import dir", 99, 999, null, default);
    }

    [Test]
    public void Run_WithImportDirectoryAndUseAncientBarriers_ThrowsEraException()
    {
        IEraEConfig eraConfig = new EraEConfig { ImportDirectory = "import dir" };
        IHistoryConfig historyConfig = new HistoryConfig { Pruning = PruningModes.UseAncientBarriers };
        EraCliRunner cliRunner = new(eraConfig, historyConfig, Substitute.For<IEraImporter>(), Substitute.For<IEraExporter>());

        Assert.That(() => cliRunner.Run(default), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Run_WithExportDirectory_CallsEraExporter()
    {
        IEraExporter eraExporter = Substitute.For<IEraExporter>();
        IEraEConfig eraConfig = new EraEConfig
        {
            ExportDirectory = "export dir",
            From = 99,
            To = 999
        };
        EraCliRunner cliRunner = new(eraConfig, new HistoryConfig(), Substitute.For<IEraImporter>(), eraExporter);

        _ = cliRunner.Run(default);

        eraExporter.Received().Export("export dir", 99, 999, default);
    }
}
