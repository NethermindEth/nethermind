// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Steps;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Exports the EIP-8347 artifacts from this node's own preimage-flat state, then exits.</summary>
/// <remarks>The node keeps running the flat backend it was configured with; no PBT state is built or read,
/// so this module registers no PBT graph at all — only the anchor pin and the step that drives it.</remarks>
internal sealed class PbtExportModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<PbtExportPersistTarget>()
        .Bind<IPersistTarget, PbtExportPersistTarget>()
        .AddStep(typeof(ExportPbtImage));
}
