// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Metadata for the detection cache, pinned content list and viewer endpoints.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, DetectionEntry>))]
[JsonSerializable(typeof(System.Collections.Concurrent.ConcurrentDictionary<string, DetectionEntry>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<PortfolioViewerMiddleware.NodeInfo>))]
[JsonSerializable(typeof(PortfolioViewerMiddleware.DetectPost))]
internal partial class PortfolioViewerJsonContext : JsonSerializerContext;
