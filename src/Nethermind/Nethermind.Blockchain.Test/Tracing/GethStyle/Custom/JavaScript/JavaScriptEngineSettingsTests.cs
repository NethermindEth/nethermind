// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing.GethStyle.Custom.JavaScript;

[TestFixture]
public class JavaScriptEngineSettingsTests
{
    [Test]
    public void Reads_environment_overrides()
    {
        Dictionary<string, string?> overrides = new()
        {
            { "NETHERMIND_JS_TRACER_MAX_NEW_SPACE_MB", "128" },
            { "NETHERMIND_JS_TRACER_MAX_OLD_SPACE_MB", "1536" },
            { "NETHERMIND_JS_TRACER_MAX_ARRAY_BUFFER_MB", "1024" },
            { "NETHERMIND_JS_TRACER_HEAP_EXPANSION_MULTIPLIER", "2.25" },
            { "NETHERMIND_JS_TRACER_MAX_HEAP_MB", "1024" },
            { "NETHERMIND_JS_TRACER_MAX_STACK_MB", "64" },
            { "NETHERMIND_JS_TRACER_GC_THRESHOLD_MB", "256" },
            { "NETHERMIND_JS_TRACER_MIN_GC_INTERVAL_MS", "50" },
            { "NETHERMIND_JS_TRACER_EXHAUSTIVE_GC", "true" },
            { "NETHERMIND_JS_TRACER_HEAP_SAMPLE_INTERVAL_MS", "5" }
        };

        try
        {
            foreach ((string key, string? value) in overrides)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            JavaScriptEngineSettings settings = JavaScriptEngineSettings.FromEnvironment();

            settings.MaxNewSpaceSizeMiB.Should().Be(128);
            settings.MaxOldSpaceSizeMiB.Should().Be(1536);
            settings.MaxArrayBufferAllocationMiB.Should().Be(1024);
            settings.HeapExpansionMultiplier.Should().Be(2.25d);
            settings.MaxHeapSizeBytes.Should().Be(1024L * 1024L * 1024L);
            settings.MaxStackUsageBytes.Should().Be(64L * 1024L * 1024L);
            settings.GarbageCollectionThresholdBytes.Should().Be(256L * 1024L * 1024L);
            settings.MinGarbageCollectionInterval.Should().Be(TimeSpan.FromMilliseconds(50));
            settings.ExhaustiveGarbageCollection.Should().BeTrue();
            settings.HeapSizeSampleInterval.Should().Be(TimeSpan.FromMilliseconds(5));
        }
        finally
        {
            foreach ((string key, _) in overrides)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    [Test]
    public void Invalid_environment_values_fall_back_to_defaults()
    {
        Dictionary<string, string?> overrides = new()
        {
            { "NETHERMIND_JS_TRACER_MAX_NEW_SPACE_MB", "-1" },
            { "NETHERMIND_JS_TRACER_MAX_OLD_SPACE_MB", "abc" },
            { "NETHERMIND_JS_TRACER_MAX_ARRAY_BUFFER_MB", "" },
            { "NETHERMIND_JS_TRACER_HEAP_EXPANSION_MULTIPLIER", "0" },
            { "NETHERMIND_JS_TRACER_MAX_HEAP_MB", "-100" },
            { "NETHERMIND_JS_TRACER_MAX_STACK_MB", "-100" },
            { "NETHERMIND_JS_TRACER_GC_THRESHOLD_MB", "-1" },
            { "NETHERMIND_JS_TRACER_MIN_GC_INTERVAL_MS", "-10" },
            { "NETHERMIND_JS_TRACER_EXHAUSTIVE_GC", "maybe" },
            { "NETHERMIND_JS_TRACER_HEAP_SAMPLE_INTERVAL_MS", "-5" }
        };

        JavaScriptEngineSettings defaults = JavaScriptEngineSettings.Default;

        try
        {
            foreach ((string key, string? value) in overrides)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            JavaScriptEngineSettings settings = JavaScriptEngineSettings.FromEnvironment();

            settings.Should().Be(defaults);
        }
        finally
        {
            foreach ((string key, _) in overrides)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }
}
