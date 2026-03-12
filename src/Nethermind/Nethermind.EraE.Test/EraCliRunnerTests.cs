// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Config;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.Logging;
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
        EraCliRunner cliRunner = new(eraConfig, eraImporter, Substitute.For<IEraExporter>(), LimboLogs.Instance);

        _ = cliRunner.Run(default);

        eraImporter.Received().Import("import dir", 99, 999, null, default);
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
        EraCliRunner cliRunner = new(eraConfig, Substitute.For<IEraImporter>(), eraExporter, LimboLogs.Instance);

        _ = cliRunner.Run(default);

        eraExporter.Received().Export("export dir", 99, 999, default);
    }
}
