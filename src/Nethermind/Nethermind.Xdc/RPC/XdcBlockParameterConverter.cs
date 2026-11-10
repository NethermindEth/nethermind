// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// Reads the XDC <c>committed</c> block tag as <see cref="BlockParameter.Finalized"/> and defers every other form to
/// <see cref="BlockParameterConverter"/>.
/// </summary>
/// <remarks>
/// The reference client resolves <c>committed</c> to the latest block committed by the XDPoS 2.0 three-chain rule, which
/// is the block <see cref="QuorumCertificateManager"/> marks as finalized. Like the reference client, the tag is
/// case-sensitive.
/// </remarks>
internal sealed class XdcBlockParameterConverter : JsonConverter<BlockParameter>
{
    private static int _registered;

    private readonly BlockParameterConverter _converter = new();

    public override bool HandleNull => true;

    /// <summary>Adds the converter to the global <see cref="EthereumJsonSerializer"/> options once per process.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            EthereumJsonSerializer.AddConverter(new XdcBlockParameterConverter());
        }
    }

    public override BlockParameter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("committed"u8)
            ? BlockParameter.Finalized
            : _converter.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, BlockParameter value, JsonSerializerOptions options) =>
        _converter.Write(writer, value, options);
}
