// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// Pins <see cref="EthereumJsonSerializer.StrictHexFormat"/> for the whole assembly, once, before any test runs.
/// </summary>
/// <remarks>
/// That property is process-global and its backing field defaults to <see langword="false"/>, while
/// <see cref="JsonRpcConfig.StrictHexFormat"/> - what a running node actually uses - defaults to
/// <see langword="true"/>. A node sets it once at startup (<c>ApiBuilder</c>); tests used to set it per fixture or
/// per test and restore the previous value afterwards, which does not compose:
///
/// <list type="bullet">
/// <item>The first restore in the run puts back the field default, <see langword="false"/>, not the config
/// default.</item>
/// <item>Fixtures run concurrently, so one fixture's teardown flips the flag out from under another fixture's
/// in-flight parse. That is #13204: <c>eth_getBalance</c> with <c>"0x00"</c> should be refused as -32602, but a
/// concurrent restore to <see langword="false"/> makes the leading zero acceptable, the module gets called, and the
/// assertion that it was not called fails. Roughly a third of runs were affected.</item>
/// <item><c>[NonParallelizable]</c> is not a fix - it keeps a test off a worker thread, it does not stop the rest of
/// the assembly running alongside it.</item>
/// </list>
///
/// Setting it once here and never restoring it means no test mutates it during the run, so there is nothing to race
/// with. A test that needs a specific strictness should say so locally instead: construct
/// <c>BlockParameterConverter(strictQuantity)</c> and register it in the options it parses with, which takes
/// precedence over the type attribute. <c>BlockParameterConverterTests</c> does exactly that.
/// </remarks>
[SetUpFixture]
public class StrictHexFormatAssemblySetup
{
    [OneTimeSetUp]
    public void PinStrictHexFormatToTheConfigDefault() =>
        EthereumJsonSerializer.StrictHexFormat = new ConfigProvider().GetConfig<IJsonRpcConfig>().StrictHexFormat;
}
