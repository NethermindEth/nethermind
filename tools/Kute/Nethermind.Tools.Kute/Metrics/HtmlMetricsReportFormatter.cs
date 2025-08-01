// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class HtmlMetricsReportFormatter : IMetricsReportFormatter
{
    private readonly Assembly _assembly;
    private readonly string _reportTemplate;
    private readonly Encoding _encoding;

    public HtmlMetricsReportFormatter()
    {
        _assembly = Assembly.GetExecutingAssembly();
        _reportTemplate = $"{_assembly.GetName().Name}.Assets.report.html";
        _encoding = Encoding.UTF8;
    }

    public async Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default)
    {
        await stream.WriteAsync(_encoding.GetBytes("<script>\nconst input =\n"), token);
        await JsonSerializer.SerializeAsync(stream, report, cancellationToken: token);
        await stream.WriteAsync(_encoding.GetBytes("\n</script>\n"), token);

        using var resourceStream = _assembly.GetManifestResourceStream(_reportTemplate)!;
        await resourceStream.CopyToAsync(stream, token);
    }
}
