// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.Era1.Test;

public class EraCliRunnerTests
{
    [Test]
    public void WhenImportDirectoryIsSpecified_ThenCallEraImporter()
    {
        IEraImporter eraImporter = Substitute.For<IEraImporter>();
        IEraConfig eraConfig = new EraConfig()
        {
            ImportDirectory = "import dir",
            From = 99,
            To = 999
        };
        EraCliRunner cliRunner = new EraCliRunner(eraConfig, eraImporter, Substitute.For<IEraExporter>(), LimboLogs.Instance);

        _ = cliRunner.Run(default);

        eraImporter.Received().Import("import dir", 99, 999, null, default);
    }

    [Test]
    public void WhenExportDirectoryIsSpecified_ThenCallEraExporter()
    {
        IEraExporter eraExporter = Substitute.For<IEraExporter>();
        IEraConfig eraConfig = new EraConfig()
        {
            ExportDirectory = "export dir",
            From = 99,
            To = 999
        };
        EraCliRunner cliRunner = new EraCliRunner(eraConfig, Substitute.For<IEraImporter>(), eraExporter, LimboLogs.Instance);

        _ = cliRunner.Run(default);

        eraExporter.Received().Export("export dir", 99, 999, default);
    }
}
