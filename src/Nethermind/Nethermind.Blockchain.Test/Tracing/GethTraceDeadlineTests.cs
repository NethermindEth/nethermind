// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using NSubstitute;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using JavaScriptDb = Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Db;

namespace Nethermind.Blockchain.Test.Tracing;

public class GethTraceDeadlineTests
{
    [TestCase("0", 0L)]
    [TestCase("-1ns", 0L)]
    [TestCase("1ns", 0L)]
    [TestCase("100ns", 1L)]
    [TestCase("+1.5us", 15L)]
    [TestCase("1µs", 10L)]
    [TestCase("1μs", 10L)]
    [TestCase(".5s", 5000000L)]
    [TestCase("1.s", 10000000L)]
    [TestCase("1h2m3.4s", 37234000000L)]
    [TestCase("2562047h47m16.854775807s", 92233720368547758L)]
    [TestCase("-2562047h47m16.854775808s", -92233720368547758L)]
    public void Go_duration_preserves_valid_range(string text, long ticks) =>
        Assert.That(GoTraceDuration.Parse(text).Ticks, Is.EqualTo(ticks));

    [TestCase("", "time: invalid duration \"\"")]
    [TestCase("bad", "time: invalid duration \"bad\"")]
    [TestCase("1", "time: missing unit in duration \"1\"")]
    [TestCase("1d", "time: unknown unit \"d\" in duration \"1d\"")]
    [TestCase("00:00:05", "time: unknown unit \":\" in duration \"00:00:05\"")]
    [TestCase("9223372036854775808ns", "time: invalid duration \"9223372036854775808ns\"")]
    public void Go_duration_rejects_non_go_formats(string text, string expected) =>
        Assert.That(() => GoTraceDuration.Parse(text), Throws.TypeOf<FormatException>().With.Message.EqualTo(expected));

    [Test]
    public void Trace_options_defer_duration_validation_and_preserve_record_clones([Values] bool withoutMetadata)
    {
        Utf8JsonReader reader = new("{\"timeout\":\"bad\",\"tracer\":\"callTracer\"}"u8);
        reader.Read();
        GethTraceOptions options = new GethTraceOptionsConverter().Read(ref reader, typeof(GethTraceOptions),
            withoutMetadata ? new JsonSerializerOptions(EthereumJsonSerializer.JsonOptions) { TypeInfoResolver = null } : EthereumJsonSerializer.JsonOptions);
        GethTraceOptions clone = options with { DisableStack = true };
        Assert.That(clone.Tracer, Is.EqualTo("callTracer"));
        Assert.That(() => clone.Timeout, Throws.TypeOf<FormatException>().With.Message.EqualTo("time: invalid duration \"bad\""));
    }

    [Test]
    public void Programmatic_timeout_round_trips_without_dotnet_duration_syntax()
    {
        GethTraceOptions options = new() { Timeout = TimeSpan.FromMinutes(3), EnableMemory = true };
        string json = JsonSerializer.Serialize(options, EthereumJsonSerializer.JsonOptions);
        GethTraceOptions roundTrip = JsonSerializer.Deserialize<GethTraceOptions>(json, EthereumJsonSerializer.JsonOptions)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTrip.Timeout, Is.EqualTo(options.Timeout));
            Assert.That(roundTrip.EnableMemory, Is.True);
        }
    }

    [Test]
    public void Deadline_expires_and_disposes_without_sleep()
    {
        ManualClock clock = new();
        using CancellationTokenSource external = new();
        using (GethTraceDeadline deadline = new(external.Token, clock))
        {
            deadline.Start(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(deadline.Expired, Is.False);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(deadline.Expired, Is.True);
                Assert.That(deadline.Token.IsCancellationRequested, Is.True);
                Assert.That(external.IsCancellationRequested, Is.False);
            }
        }
        Assert.That(clock.Timer!.Disposed, Is.True);
    }

    [Test]
    public void Deadline_chunks_large_go_durations()
    {
        ManualClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        deadline.Start(TimeSpan.FromDays(100));
        Assert.That(clock.Timer!.Due, Is.EqualTo(TimeSpan.FromMilliseconds(uint.MaxValue - 1)));
        clock.Advance(TimeSpan.FromDays(50));
        Assert.That(deadline.Expired, Is.False);
        clock.Advance(TimeSpan.FromDays(50));
        Assert.That(deadline.Expired, Is.True);
    }

    [Test]
    public void External_cancellation_is_not_a_request_deadline()
    {
        ManualClock clock = new();
        using CancellationTokenSource external = new();
        using GethTraceDeadline deadline = new(external.Token, clock);
        deadline.Start(TimeSpan.FromSeconds(1));
        external.Cancel();
        clock.Advance(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deadline.Token.IsCancellationRequested, Is.True);
            Assert.That(deadline.Expired, Is.False);
        }
    }

    [Test]
    public void Shared_deadline_interrupts_an_executing_js_callback([Values] bool cloneOptions)
    {
        ManualClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        IWorldState state = Substitute.For<IWorldState>();
        state.GetNonce(Arg.Any<Address>()).Returns(_ => { clock.Advance(TimeSpan.FromSeconds(1)); return 0UL; });
        Engine engine = new(Cancun.Instance);
        GethTraceCancellation cancellation = new();
        GethTraceOptions options = new()
        {
            ExecutionCancellation = cancellation,
            Tracer = "{fault:function(){},result:function(ctx,db){db.getNonce('0x0000000000000000000000000000000000000001');for(var i=0;i<100000;i++){}return {};}}"
        };
        if (cloneOptions) options = options with { DisableStack = true };
        cancellation.Token = deadline.Token;
        using GethLikeJavaScriptTxTracer tracer = new(engine, new JavaScriptDb(state), new Context(), options);
        deadline.Start(TimeSpan.FromSeconds(1));
        Assert.That(() => tracer.BuildResult(), Throws.InstanceOf<ScriptInterruptedException>());
        Assert.That(deadline.Expired, Is.True);
    }

    [Test]
    public void Already_cancelled_execution_does_not_construct_js_tracer()
    {
        using CancellationTokenSource external = new();
        external.Cancel();
        using Engine engine = new(Cancun.Instance);
        Assert.That(() => new GethLikeJavaScriptTxTracer(engine, new JavaScriptDb(Substitute.For<IWorldState>()), new Context(), new GethTraceOptions
        {
            ExecutionCancellation = new GethTraceCancellation { Token = external.Token },
            Tracer = "(()=>{throw Error('must not execute');})()"
        }), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task External_cancellation_interrupts_js_construction([Values] bool setup)
    {
        using CancellationTokenSource external = new();
        using Engine engine = new(Cancun.Instance);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dynamic hooks = engine.CreateTracer("{hooks: globalThis.cancellationTest = {}}").hooks;
        hooks.started = new Action(() => entered.TrySetResult());
        string script = setup
            ? "{fault:function(){},result:function(){return {};},setup:function(){cancellationTest.started();while(true){}}}"
            : "{fault:function(){},result:function(){return {};},value:(()=>{cancellationTest.started();while(true){}})()}";
        Task<Exception?> construction = Task.Run(() =>
        {
            try
            {
                using (new GethLikeJavaScriptTxTracer(engine, new JavaScriptDb(Substitute.For<IWorldState>()), new Context(), new GethTraceOptions
                {
                    ExecutionCancellation = new GethTraceCancellation { Token = external.Token },
                    Tracer = script
                }))
                {
                    return (Exception?)null;
                }
            }
            catch (Exception exception) { return exception; }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            external.Cancel();
            Assert.That(await construction.WaitAsync(TimeSpan.FromSeconds(5)), Is.InstanceOf<ScriptInterruptedException>());
        }
        finally
        {
            engine.Interrupt();
            await construction.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase("{fault:function(){}}")]
    [TestCase("{fault:function(){},result:function(){return {};},setup:function(){throw Error('setup failed');}}")]
    public void Failed_js_construction_releases_external_cancellation_registration(string script)
    {
        using CancellationTokenSource external = new();
        using Engine engine = new(Cancun.Instance);
        Assert.That(() => new GethLikeJavaScriptTxTracer(engine, new JavaScriptDb(Substitute.For<IWorldState>()), new Context(), new GethTraceOptions
        {
            ExecutionCancellation = new GethTraceCancellation { Token = external.Token },
            Tracer = script
        }), script == "{fault:function(){}}" ? Throws.TypeOf<ArgumentException>() : Throws.InstanceOf<Exception>());
        external.Cancel();
        Assert.That(() => engine.CreateTracer("{fault:function(){},result:function(){return {};}}"), Throws.Nothing);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        internal ManualTimer? Timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new(callback, state) { Due = dueTime };
        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            Timer!.Fire();
        }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        internal TimeSpan Due;
        internal bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !Disposed; }
        internal void Fire() { if (!Disposed) callback(state); }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
