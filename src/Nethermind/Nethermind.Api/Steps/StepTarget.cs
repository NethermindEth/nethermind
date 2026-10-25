// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps;

/// <summary>Selects the step that is the goal of this run, by its step base type.</summary>
/// <remarks>Registered by a module that decided from configuration that the run is a one-shot job.</remarks>
public sealed record StepTarget(Type StepBaseType);

/// <summary>Selects the goal of this run by command name, resolved against the registered steps.</summary>
/// <remarks>
/// Registered from the command line, where only the name is known; the name is matched against
/// <see cref="StepInfo.Command"/> once the steps have been resolved.
/// </remarks>
public sealed record StepCommandSelection(string Name);
