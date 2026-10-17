// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm;

namespace Nethermind.IlEvm;

/// <summary>
/// The live model's constants for one source: run compiled, a visit of x opcodes saves <c>a·x − g − b·Cold(d)·min(x, U)</c>
/// with <c>Cold(d) = d/(d + d0)</c>, per shape and kind of thread: block processing's own (M, index 0) or the other
/// threads its runner serves (O, index 1); the read-only queries count kind M only.
/// </summary>
internal sealed class IlModel
{
    public static readonly IlModel BlocksPrior = new([0.25, 0.6, 1.1, 0.25, 0.6, 1.1], [25, 25], [1.5, 1.5], u: 300, d0: 32, rho: 0.25, lambda: 0, tau: 0);

    /// <summary>As block processing's, but the queries meet their code cold from memory rather than from the shared caches.</summary>
    public static readonly IlModel RpcPrior = new([0.25, 0.6, 1.1, 0.25, 0.6, 1.1], [25, 25], [10, 10], u: 300, d0: 32, rho: 0.25, lambda: 0, tau: 0);

    private readonly double[] _a;

    /// <summary>A constant set: <paramref name="a"/> by kind then shape, <paramref name="g"/> and <paramref name="b"/> by kind.</summary>
    public IlModel(double[] a, double[] g, double[] b, double u, double d0, double rho, double lambda, double tau)
    {
        _a = a;
        G = g;
        B = b;
        U = Math.Max(0, u);
        D0 = Math.Max(1e-9, d0);
        Rho = Math.Clamp(rho, 0, 1);
        Lambda = Math.Max(0, lambda);
        Tau = Math.Max(0, tau);
    }

    /// <summary>The fixed cost of entering compiled code, per visit, in nanoseconds, by kind.</summary>
    public readonly double[] G;

    /// <summary>The cost of meeting compiled code cold, per opcode of it a visit runs up to <see cref="U"/>, in nanoseconds, by kind.</summary>
    public readonly double[] B;

    /// <summary>The distinct opcodes a visit touches at most: past them a visit loops over code it has met.</summary>
    public readonly double U;

    /// <summary>The other visits between two of a code's at which it is half cold.</summary>
    public readonly double D0;

    /// <summary>How much of what the other threads save shortens block processing.</summary>
    public readonly double Rho;

    /// <summary>The footprint's charge, in nanoseconds per epoch per MiB of IL, tables and native code.</summary>
    public readonly double Lambda;

    /// <summary>The model's residual, in nanoseconds per epoch, which a check's measurements shrink.</summary>
    public readonly double Tau;

    /// <summary>The warm saving per opcode, in nanoseconds.</summary>
    public double A(IlEvmCodeShape shape, int kind) => _a[kind * 3 + (int)shape];

    /// <summary>
    /// <paramref name="prior"/> with the constants the tokens after <paramref name="prefix"/> name, case-insensitively:
    /// <c>a.line=</c>, <c>a.mixed=</c>, <c>a.loop=</c>, <c>g=</c> and <c>b=</c> for both kinds or, with <c>.M</c> or
    /// <c>.O</c>, one; <c>U=</c>, <c>d0=</c>, <c>rho=</c>, <c>lambda=</c>, <c>tau=</c>. Non-finite values are skipped.
    /// </summary>
    public static IlModel From(IlModel prior, IReadOnlyDictionary<string, string> tokens, string prefix)
    {
        double[] a = [.. prior._a], g = [.. prior.G], b = [.. prior.B];
        double u = prior.U, d0 = prior.D0, rho = prior.Rho, lambda = prior.Lambda, tau = prior.Tau;
        string[] shapes = ["line", "mixed", "loop"], kinds = ["M", "O"];
        for (int shape = 0; shape < shapes.Length; shape++)
        {
            if (Value(tokens, $"{prefix}a.{shapes[shape]}") is { } both)
                a[shape] = a[3 + shape] = both;
            for (int kind = 0; kind < kinds.Length; kind++)
            {
                if (Value(tokens, $"{prefix}a.{shapes[shape]}.{kinds[kind]}") is { } one)
                    a[kind * 3 + shape] = one;
            }
        }

        foreach ((string name, double[] values) in new[] { ("g", g), ("b", b) })
        {
            if (Value(tokens, prefix + name) is { } both)
                values[0] = values[1] = both;
            for (int kind = 0; kind < kinds.Length; kind++)
            {
                if (Value(tokens, $"{prefix}{name}.{kinds[kind]}") is { } one)
                    values[kind] = one;
            }
        }

        return new IlModel(a, g, b, Value(tokens, prefix + "U") ?? u, Value(tokens, prefix + "d0") ?? d0, Value(tokens, prefix + "rho") ?? rho,
            Value(tokens, prefix + "lambda") ?? lambda, Value(tokens, prefix + "tau") ?? tau);

        static double? Value(IReadOnlyDictionary<string, string> tokens, string name) =>
            tokens.TryGetValue(name, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                ? value
                : null;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"a {_a[0]}/{_a[1]}/{_a[2]} M, {_a[3]}/{_a[4]}/{_a[5]} O ns/op, g {G[0]}/{G[1]} ns, b {B[0]}/{B[1]} ns/op, U {U}, d0 {D0}, rho {Rho}, lambda {Lambda}, tau {Tau}");
}

/// <summary>One source's live profiling thresholds and constants, block processing's or the read-only queries'; immutable.</summary>
internal sealed class IlLiveParams
{
    public static IlLiveParams BlocksPrior => new(block: true, minEpochs: 8, minMeasured: 32, thetaUp: 5_000, halfLife: 512, IlModel.BlocksPrior);

    /// <summary>The read-only queries': the threshold is about the Legacy rules' 50,000 opcodes a second at a nanosecond each.</summary>
    public static IlLiveParams RpcPrior => new(block: false, minEpochs: 2, minMeasured: 16, thetaUp: 50_000, halfLife: 600, IlModel.RpcPrior);

    public IlLiveParams(bool block, int minEpochs, int minMeasured, double thetaUp, double halfLife, IlModel model)
    {
        Block = block;
        MinEpochs = Math.Max(0, minEpochs);
        MinMeasured = Math.Max(0, minMeasured);
        ThetaUp = Math.Max(0, thetaUp);
        HalfLife = Math.Clamp(halfLife, 1, 1e9);
        Model = model;
        Retain = Math.Pow(2, -1 / HalfLife);
        RestartEpochs = (int)Math.Min(int.MaxValue, Math.Ceiling(4 * HalfLife));
    }

    public readonly bool Block;

    /// <summary>The epochs a code must have been counted in before it is promoted.</summary>
    public readonly int MinEpochs;

    /// <summary>The measured visits, decayed, a code must have before it is promoted.</summary>
    public readonly int MinMeasured;

    /// <summary>What compiling must save to promote, in nanoseconds per epoch.</summary>
    public readonly double ThetaUp;

    public readonly double HalfLife;

    /// <summary>The silence after which a code counted again is counted afresh, its rate and epochs from then.</summary>
    public readonly int RestartEpochs;

    public readonly IlModel Model;

    public readonly double Retain;

    public string Name => Block ? "blocks" : "rpc";

    /// <summary>The epochs a demoted code, or one whose layout failed, waits before it may be promoted again.</summary>
    public long CooldownEpochs => (long)Math.Round(10 * HalfLife);

    public IlLiveParams With(int? minEpochs = null, int? minMeasured = null, double? thetaUp = null, double? halfLife = null, IlModel? model = null) =>
        new(Block, minEpochs ?? MinEpochs, minMeasured ?? MinMeasured, thetaUp ?? ThetaUp, halfLife ?? HalfLife, model ?? Model);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}: theta {ThetaUp} ns, min epochs {MinEpochs}, min measured {MinMeasured}, half-life {HalfLife}; {Model}");
}

/// <summary>
/// Live profiling: the counts of the one source <see cref="IIlEvmConfig.Prefer"/> picks, and the scorer that, between
/// blocks, promotes the codes compiling would gain most on and demotes those it no longer gains on.
/// </summary>
/// <remarks>
/// The counting runner samples one transaction in <see cref="IlGate.QuerySampleEvery"/> on the queries' runner and one in
/// <see cref="IlGate.BlockSampleEvery"/> on block processing's. Each visit its gate folds (<see cref="IlThread"/>) is
/// counted once, never per part, standing for the visits sampling left out: the transactions not sampled, times the
/// visit's own sampling (a measured settled visit stands for 8, a compiled, renting or uncompiled one for itself). Only
/// the counting runner counts, so neither source writes the other's. The scorer, <see cref="Tick"/>, runs on a scheduler
/// worker, never beside a step. Shipped contracts are never promoted or demoted here.
/// </remarks>
internal static class IlLive
{
    /// <summary>The log2 buckets of visit length: fewer than 64 opcodes, then each power of two, the last from 65,536.</summary>
    public const int Buckets = 12;

    public const int PartsIndex = 48;

    /// <summary>The measured visits, unweighted: the evidence behind the rest.</summary>
    public const int MeasuredIndex = 49;

    public const int CounterCount = 50;

    /// <summary>About the IL and tables a contract takes per byte of code: the budget's estimate until its layout says.</summary>
    public const double IlBytesPerCodeByte = 52.7;

    /// <summary>About the IL, tables and native code a contract takes per byte of code, for the footprint's charge.</summary>
    public const double FootprintBytesPerCodeByte = 90;

    /// <summary>The epochs a profile nothing compiled for must go untouched before the table, near its cap, may let it go.</summary>
    public const int UntouchedEpochs = 1_024;

    public const int MaxCandidates = 1_024;

    /// <summary>The touched profiles a tick folds whatever its time, so it always gets on.</summary>
    private const int MinDrain = 64;

    /// <summary>The profiles a step of an eviction scan looks at whatever its time.</summary>
    private const int MinScan = 1_024;

    /// <summary>Ticks running a live code must score under a quarter of the threshold to be demoted.</summary>
    public const int DemoteTicks = 3;

    private static IlLiveParams _rpc = Overridden(IlLiveParams.RpcPrior, IlModel.RpcPrior, []);
    private static IlLiveParams _blocks = Overridden(IlLiveParams.BlocksPrior, IlModel.BlocksPrior, []);

    public static IlLiveParams Rpc
    {
        get => Volatile.Read(ref _rpc);
        set => Volatile.Write(ref _rpc, value);
    }

    public static IlLiveParams Blocks
    {
        get => Volatile.Read(ref _blocks);
        set => Volatile.Write(ref _blocks, value);
    }

    public static IlLiveParams For(bool block) => block ? Blocks : Rpc;

    /// <summary>The IL and tables the contracts live profiling compiled may hold, <see cref="IIlEvmConfig.LiveBudgetMiB"/>; <see cref="IlEvmSettings.MaxLiveIlBytes"/> stays the hard cap.</summary>
    public static long BudgetBytes { get; set; } = 128L << 20;

    /// <summary>How long a tick may take, in milliseconds, before it leaves the rest to the next.</summary>
    public static double TickBudgetMs { get; set; } = 1;

    /// <summary>The profiles counted since the scorer last folded them, each once.</summary>
    internal static readonly ConcurrentQueue<IlCodeProfile> Touched = new();

    // The scorer's own, on the scheduler worker that holds the maintenance.
    private static readonly List<IlCodeProfile> _live = [];
    private static readonly List<IlCodeProfile> _candidates = [];
    private static readonly double[] _allVisits = new double[2];
    private static int _allEpoch;
    private static IEnumerator<KeyValuePair<IlProfileKey, IlCodeProfile>>? _scan;
    private static List<(double Ops, IlCodeProfile Profile)>? _evictable;

    /// <summary>The IL and tables of the live contracts, laid out or reserved for a layout: the queries' at 0, block processing's at 1.</summary>
    private static readonly long[] _liveBytes = new long[2];

    private static readonly long[] _liveCount = new long[2];

    private static long _promoted, _demoted, _displaced, _released, _failed, _deferred, _evicted, _folds, _scored, _ticks, _forkMoves;

    /// <summary>Counts a folded visit on <paramref name="profile"/>, standing for <paramref name="weight"/> visits.</summary>
    /// <param name="main">Block processing's own thread or any query thread; else block processing's other threads.</param>
    /// <param name="block">Whether block processing's runner counted it, in whose epochs it counts.</param>
    /// <remarks>Racy plain adds on the frame's thread, once per visit: two threads adding at once may lose a count, which only adds noise.</remarks>
    public static void Count(IlCodeProfile profile, int ops, int parts, bool main, int weight, bool block)
    {
        int epoch = Epoch(block);
        long[] counters = Volatile.Read(ref profile.Counters) ?? Start(profile, epoch);
        int at = ((main ? 0 : Buckets) + BucketOf(ops)) * 2;
        counters[at] += weight;
        counters[at + 1] += (long)ops * weight;
        counters[PartsIndex] += (long)parts * weight;
        counters[MeasuredIndex]++;
        if (profile.LastEpoch != epoch)
            NewEpoch(profile, epoch, block);

        if (Volatile.Read(ref profile.TouchedEpoch) != epoch && Interlocked.Exchange(ref profile.TouchedEpoch, epoch) != epoch &&
            Interlocked.Exchange(ref profile.InTouched, 1) == 0)
            Touched.Enqueue(profile);
    }

    /// <summary>The code's first count in an epoch; after <see cref="IlLiveParams.RestartEpochs"/> of silence the history has all but decayed, so it counts afresh.</summary>
    private static void NewEpoch(IlCodeProfile profile, int epoch, bool block)
    {
        if (profile.LastEpoch >= 0 && epoch - profile.LastEpoch >= For(block).RestartEpochs)
        {
            profile.FirstEpoch = epoch;
            profile.Epochs = 0;
        }

        profile.LastEpoch = epoch;
        profile.Epochs++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long[] Start(IlCodeProfile profile, int epoch)
    {
        // Before the counters that publish it: the scorer reads it once it sees them.
        profile.FirstEpoch = epoch;
        long[] created = new long[CounterCount];
        return Interlocked.CompareExchange(ref profile.Counters, created, null) ?? created;
    }

    public static int BucketOf(long ops) => Math.Clamp(BitOperations.Log2((ulong)Math.Max(ops, 1)) - 5, 0, Buckets - 1);

    public static int Epoch(bool block) => block ? Volatile.Read(ref IlRuntime.BlockEpoch) : IlRuntime.RpcEpoch;

    /// <summary>The source the scorer serves; null with <see cref="IlEvmPrefer.None"/> or under rules other than the Adaptive ones.</summary>
    public static IlLiveParams? Active => IlEvmSettings.Mode != IlMode.Adaptive ? null : IlEvmSettings.Prefer switch
    {
        IlEvmPrefer.Rpc => Rpc,
        IlEvmPrefer.Blocks => Blocks,
        _ => null,
    };

    /// <summary>The scorer, on the scheduler's worker at each window's opening and once a second.</summary>
    /// <param name="nowMs">Unused: the scorer goes by its source's epochs.</param>
    public static void Tick(long nowMs)
    {
        IlLiveParams? source = Active;
        if (source is null)
            return;
        long started = Stopwatch.GetTimestamp();
        long budget = (long)(TickBudgetMs * Stopwatch.Frequency / 1000);
        int epoch = Epoch(source.Block);
        _ticks++;

        for (int drained = 0; (drained < MinDrain || Stopwatch.GetTimestamp() - started < budget) && Touched.TryDequeue(out IlCodeProfile? profile); drained++)
        {
            Volatile.Write(ref profile.InTouched, 0);
            if (profile.State == IlCodeProfile.LiveEvicted || !Fold(profile, epoch, source) || profile.InLive || profile.InCandidates || !Promotable(profile, source))
                continue;
            Rescore(profile, epoch, source);
            if (Eligible(profile, epoch, source))
                AddCandidate(profile);
        }

        Review(epoch, source);
        Choose(source);
        EvictStep(epoch, source, started, budget);
    }

    internal static bool Fold(IlCodeProfile profile, int epoch, IlLiveParams source)
    {
        long[]? counters = Volatile.Read(ref profile.Counters);
        if (counters is null)
            return false;
        bool first = profile.Hist is null;
        double[] history = profile.Hist ??= new double[CounterCount];
        long[] folded = profile.Folded ??= new long[CounterCount];
        // Counted afresh after a long silence: the little left of the history before it would swell the new rate.
        double keep = first || profile.FirstEpoch > profile.HistEpoch ? 0 : Keep(epoch - profile.HistEpoch, source);
        double main = 0, others = 0;
        for (int i = 0; i < CounterCount; i++)
        {
            long now = Volatile.Read(ref counters[i]);
            // A racy add may set a counter back: what it lost was counted already, and the next fold counts from here.
            long delta = Math.Max(0, now - folded[i]);
            folded[i] = now;
            history[i] = history[i] * keep + delta;
            if (i < PartsIndex && (i & 1) == 0)
            {
                if (i < Buckets * 2)
                    main += delta;
                else
                    others += delta;
            }
        }

        profile.HistEpoch = epoch;
        double all = Keep(epoch - _allEpoch, source);
        _allVisits[0] = _allVisits[0] * all + main;
        _allVisits[1] = _allVisits[1] * all + others;
        _allEpoch = Math.Max(_allEpoch, epoch);
        _folds++;
        return true;
    }

    /// <summary>What <paramref name="epochs"/> epochs keep of a count: all of it for none, or for a clock a test set back.</summary>
    internal static double Keep(long epochs, IlLiveParams source) => epochs <= 0 ? 1 : Math.Pow(2, -epochs / source.HalfLife);

    /// <summary>What turns a history into a rate, so a code counted for a few epochs is not diluted over a half-life it was not around for.</summary>
    internal static double Window(long epochs, IlLiveParams source) =>
        epochs <= 1 ? 1 : (1 - Math.Pow(source.Retain, epochs)) / (1 - source.Retain);

    internal static double Decayed(IlCodeProfile profile, int index, int epoch, IlLiveParams source) =>
        profile.Hist is { } history ? history[index] * Keep(epoch - profile.HistEpoch, source) : 0;

    /// <summary>The decayed visits of a kind over every profile: the reuse distance's numerator.</summary>
    internal static double AllVisits(int kind, int epoch, IlLiveParams source) => _allVisits[kind] * Keep(epoch - _allEpoch, source);

    internal static IlEvmCodeShape ShapeOf(double opsPerVisit, int size)
    {
        double passes = opsPerVisit / Math.Max(1, size);
        return passes < 0.1 ? IlEvmCodeShape.Line : passes >= 1 ? IlEvmCodeShape.Loop : IlEvmCodeShape.Mixed;
    }

    internal static double AdmissionOps(IlCodeProfile profile, bool block)
    {
        if (!IlEvmSettings.Gate)
            return 0;
        if (!block)
            return IlGate.Query.VisitColdOps;
        return profile.BlockMinVisitLog2 > 0 ? Math.Pow(2, Math.Clamp((int)profile.BlockMinVisitLog2, 2, 30)) : IlGate.Block.VisitColdOps;
    }

    internal static double FootprintMiB(int size) => Math.Max(1, size) * FootprintBytesPerCodeByte / (1 << 20);

    internal static long EstimateBytes(int size) => (long)Math.Ceiling(Math.Max(1, size) * IlBytesPerCodeByte);

    /// <summary>Scores a profile: its gate-aware saving per epoch, blended with its check's measurement, its noise and its density per MiB.</summary>
    internal static void Rescore(IlCodeProfile profile, int epoch, IlLiveParams source)
    {
        _scored++;
        double[]? history = profile.Hist;
        if (history is null)
        {
            profile.Score = profile.Density = profile.Measures = 0;
            profile.Sigma = double.PositiveInfinity;
            return;
        }

        IlModel model = source.Model;
        double keep = Keep(epoch - profile.HistEpoch, source);
        // Per epoch from the first count's, at whatever the history has decayed to.
        double rate = keep / Window(epoch - profile.FirstEpoch + 1, source);
        double visits = 0, ops = 0;
        for (int i = 0; i < PartsIndex; i += 2)
        {
            visits += history[i];
            ops += history[i + 1];
        }

        IlEvmCodeShape shape = visits > 0 ? ShapeOf(ops / visits, profile.Size) : profile.Shape ?? IlEvmCodeShape.Mixed;
        double admission = AdmissionOps(profile, source.Block);
        double footprint = FootprintMiB(profile.Size);
        double saved = 0, admitted = 0;
        // The queries' runner counts one kind; block processing's two, the other threads' at rho.
        for (int kind = 0; kind < (source.Block ? 2 : 1); kind++)
        {
            double kindVisits = 0;
            for (int bucket = 0; bucket < Buckets; bucket++)
                kindVisits += history[(kind * Buckets + bucket) * 2];
            if (kindVisits <= 0)
                continue;
            double distance = AllVisits(kind, epoch, source) / (kindVisits * keep);
            double cold = distance / (distance + model.D0);
            double a = model.A(shape, kind), entry = model.G[kind], miss = model.B[kind] * cold;
            double weight = kind == 0 ? 1 : model.Rho;
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                int at = (kind * Buckets + bucket) * 2;
                double v = history[at] * rate, o = history[at + 1] * rate;
                if (v <= 0)
                    continue;
                double mean = o / v;
                // Only visits the gate would admit cold count; those it would admit warm, after a compiled visit, are left out.
                if (mean < admission)
                    continue;
                admitted += weight * o;
                double gain = a * o - v * (entry + miss * Math.Min(mean, model.U));
                if (gain > 0)
                    saved += weight * gain;
            }
        }

        double modelled = saved - model.Lambda * footprint;
        double score = modelled;
        long pairs = 0;
        // A live contract its check has timed: what the check measured, per opcode it admits, weighs in by its pairs.
        if (profile.Live is { } live && (source.Block ? live.BlockCheck : live.QueryCheck) is { } check)
        {
            (pairs, double interpreted, double compiled) = check.Measured();
            if (pairs > 0 && interpreted > 0)
            {
                double measured = admitted * (interpreted - compiled) - model.Lambda * footprint;
                score = (pairs * measured + 16 * modelled) / (pairs + 16);
            }
            else
            {
                pairs = 0;
            }
        }

        double measures = history[MeasuredIndex] * keep;
        double noise = measures > 0 ? Math.Abs(score) / Math.Sqrt(measures) : double.PositiveInfinity;
        double residual = model.Tau * 16 / (pairs + 16);
        profile.Score = score;
        profile.Sigma = Math.Sqrt(noise * noise + residual * residual);
        profile.Measures = measures;
        profile.Density = score / footprint;
    }

    private static bool Promotable(IlCodeProfile profile, IlLiveParams source) =>
        profile.State is IlCodeProfile.LiveNone or IlCodeProfile.LiveDemoted && profile.Live is null && profile.LastBodies is { } bodies &&
        !ShippedFor(profile, bodies) && !(source.Block && IlEvmSettings.BlockDeclineAll) && HasCode(profile);

    private static bool ShippedFor(IlCodeProfile profile, OpcodeBodySet bodies) =>
        ReferenceEquals(profile.Shipped?.Bodies, bodies) || ReferenceEquals(profile.ShippedNext?.Bodies, bodies);

    private static bool HasCode(IlCodeProfile profile) =>
        profile.Key.IsByContent ? !profile.Retained.IsEmpty : IlScheduler.Current?.CodeDb is not null;

    private static bool Eligible(IlCodeProfile profile, int epoch, IlLiveParams source) =>
        profile.Score - source.ThetaUp >= 2 * profile.Sigma && profile.Epochs >= source.MinEpochs && profile.Measures >= source.MinMeasured &&
        (profile.State != IlCodeProfile.LiveDemoted || epoch >= profile.CooldownUntil || profile.Score > 4 * source.ThetaUp);

    private static void AddCandidate(IlCodeProfile profile)
    {
        if (_candidates.Count >= MaxCandidates)
        {
            // Full: the newcomer replaces the least dense, if it is denser.
            int least = 0;
            for (int i = 1; i < _candidates.Count; i++)
            {
                if (_candidates[i].Density < _candidates[least].Density)
                    least = i;
            }

            if (_candidates[least].Density >= profile.Density)
                return;
            _candidates[least].InCandidates = false;
            _candidates.RemoveAt(least);
        }

        profile.InCandidates = true;
        _candidates.Add(profile);
    }

    /// <summary>Lets go of live codes no longer promoted, as after a failed layout or a check's release; rescores the rest and the candidates.</summary>
    private static void Review(int epoch, IlLiveParams source)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            IlCodeProfile profile = _live[i];
            if (profile.State == IlCodeProfile.LiveQueued)
                continue;
            IlEvm.IlContract? live = profile.Live;
            if (profile.State != IlCodeProfile.LivePromoted || live is null)
            {
                Drop(i);
                continue;
            }

            // A list leftover compiled between blocks for the same body set takes over.
            if (ShippedFor(profile, live.Bodies))
            {
                IlCheck.ReleaseNow(live);
                profile.State = IlCodeProfile.LiveNone;
                Drop(i);
                continue;
            }

            if (profile.LastBodies is { } bodies && !ReferenceEquals(live.Bodies, bodies))
            {
                // A fork moved the code's body set and no part runs the contract any more; an occasional resolution on
                // another one, as a call on an old block's state makes, does not.
                if (++profile.MovedTicks < DemoteTicks)
                    continue;
                IlCheck.ReleaseNow(live);
                profile.State = IlCodeProfile.LiveNone;
                _forkMoves++;
                Drop(i);
                continue;
            }

            profile.MovedTicks = 0;

            Fold(profile, epoch, source);
            Rescore(profile, epoch, source);
            if (profile.Score >= source.ThetaUp / 4)
            {
                profile.BelowTicks = 0;
                continue;
            }

            if (++profile.BelowTicks < DemoteTicks)
                continue;
            Demote(live);
            Drop(i);
        }

        for (int i = _candidates.Count - 1; i >= 0; i--)
        {
            IlCodeProfile profile = _candidates[i];
            if (profile.State == IlCodeProfile.LiveEvicted || !Promotable(profile, source))
            {
                profile.InCandidates = false;
                _candidates.RemoveAt(i);
                continue;
            }

            Fold(profile, epoch, source);
            Rescore(profile, epoch, source);
            if (Eligible(profile, epoch, source))
                continue;
            profile.InCandidates = false;
            _candidates.RemoveAt(i);
        }
    }

    private static void Drop(int index)
    {
        _live[index].InLive = false;
        _live[index].BelowTicks = _live[index].MovedTicks = 0;
        _live.RemoveAt(index);
    }

    /// <summary>A live code demoted: its contract released, its profile in a cooldown of ten half-lives.</summary>
    private static void Demote(IlEvm.IlContract live)
    {
        IlScheduler.Release(live);
        _demoted++;
    }

    /// <summary>A live code released to make room for a denser one: nothing was found against it, so it takes no cooldown.</summary>
    private static void Displace(IlEvm.IlContract live)
    {
        if (live.Profile is { } profile && ReferenceEquals(profile.Live, live))
            profile.State = IlCodeProfile.LiveNone;
        IlCheck.ReleaseNow(live);
        _displaced++;
    }

    /// <summary>Promotes candidates greedily by score per MiB within the budget; one that does not fit displaces live codes at most half as dense.</summary>
    private static void Choose(IlLiveParams source)
    {
        if (_candidates.Count == 0)
            return;
        _candidates.Sort(static (x, y) => y.Density.CompareTo(x.Density));
        int index = source.Block ? 1 : 0;
        // The live codes laid out, the least dense first: made once a tick, when a candidate first does not fit.
        List<IlCodeProfile>? laidOut = null;
        for (int i = 0; i < _candidates.Count; i++)
        {
            IlCodeProfile candidate = _candidates[i];
            long estimate = EstimateBytes(candidate.Size);
            if (!Fits(index, estimate, freed: 0))
            {
                laidOut ??= LaidOut();
                int displaced = laidOut.Count == 0 || candidate.Density < 2 * laidOut[0].Density ? 0 : Displaced(candidate, laidOut, index, estimate);
                if (displaced == 0)
                    continue;
                for (int victim = 0; victim < displaced; victim++)
                {
                    Displace(laidOut[victim].Live!);
                    Drop(_live.IndexOf(laidOut[victim]));
                }

                laidOut.RemoveRange(0, displaced);
            }

            if (!Fits(index, estimate, freed: 0) || !Promote(candidate, source))
                continue;
            candidate.InCandidates = false;
            _candidates.RemoveAt(i--);
        }
    }

    private static bool Fits(int index, long bytes, long freed) =>
        Interlocked.Read(ref _liveBytes[index]) - freed + bytes <= BudgetBytes && IlEvmSettings.LiveIlBytes - freed + bytes <= IlEvmSettings.MaxLiveIlBytes;

    private static List<IlCodeProfile> LaidOut()
    {
        List<IlCodeProfile> laidOut = [];
        foreach (IlCodeProfile profile in _live)
        {
            if (profile.State == IlCodeProfile.LivePromoted && profile.Live is not null)
                laidOut.Add(profile);
        }

        laidOut.Sort(static (x, y) => x.Density.CompareTo(y.Density));
        return laidOut;
    }

    /// <summary>How many of the least dense live codes must go to make room for <paramref name="candidate"/>; 0 when they cannot, so none is released in vain.</summary>
    private static int Displaced(IlCodeProfile candidate, List<IlCodeProfile> laidOut, int index, long estimate)
    {
        long freed = 0;
        for (int i = 0; i < laidOut.Count && candidate.Density >= 2 * laidOut[i].Density; i++)
        {
            freed += Volatile.Read(ref laidOut[i].Live!.BudgetBytes);
            if (Fits(index, estimate, freed))
                return i + 1;
        }

        return 0;
    }

    /// <summary>Queues the layout that promotes <paramref name="profile"/> for its last body set, its budget reserved meanwhile.</summary>
    private static bool Promote(IlCodeProfile profile, IlLiveParams source)
    {
        IlScheduler? scheduler = IlScheduler.Current;
        OpcodeBodySet? bodies = profile.LastBodies;
        if (scheduler is null || bodies is null)
            return false;
        long estimate = EstimateBytes(profile.Size);
        bool block = source.Block;
        IDb? codeDb = scheduler.CodeDb;
        LayoutJob job = new(() => CodeOf(profile, codeDb), bodies, (contract, reason) => Published(profile, block, estimate, contract, reason), profile.Density);
        profile.State = IlCodeProfile.LiveQueued;
        Interlocked.Add(ref _liveBytes[block ? 1 : 0], estimate);
        if (!scheduler.Enqueue(job))
        {
            Interlocked.Add(ref _liveBytes[block ? 1 : 0], -estimate);
            profile.State = IlCodeProfile.LiveNone;
            return false;
        }

        profile.InLive = true;
        profile.BelowTicks = 0;
        _live.Add(profile);
        return true;
    }

    private static ReadOnlyMemory<byte> CodeOf(IlCodeProfile profile, IDb? codeDb)
    {
        if (profile.Key.IsByContent)
            return profile.Retained;
        ValueHash256 hash = profile.Key.Keccak;
        byte[]? code = codeDb?.Get(hash.Bytes);
        return code is null || code.Length != profile.Key.Length ? ReadOnlyMemory<byte>.Empty : code;
    }

    /// <summary>
    /// A promotion's layout ended, on a scheduler worker. The contract's profile, origin, run mask and checks are set
    /// before the profile takes it, and only then do the states resolve again; a failed layout waits a cooldown.
    /// </summary>
    internal static void Published(IlCodeProfile profile, bool block, long estimate, IlEvm.IlContract? contract, string? reason)
    {
        int index = block ? 1 : 0;
        IlLiveParams source = For(block);
        if (contract is null)
        {
            Interlocked.Add(ref _liveBytes[index], -estimate);
            Interlocked.Increment(ref _failed);
            IlEvmSettings.CountFailed(reason);
            profile.State = IlCodeProfile.LiveDemoted;
            profile.CooldownUntil = Epoch(block) + source.CooldownEpochs;
            return;
        }

        // Stale: taken back meanwhile, as by a reset, or shipped by a list leftover. Over the hard cap, which the estimate
        // missed: retried after a cooldown. The cap is checked and the bytes counted in one step, so publications on
        // several workers never pass it together.
        bool stale = profile.State != IlCodeProfile.LiveQueued || ShippedFor(profile, contract.Bodies);
        bool overCap = !stale && !contract.TryMarkLive();
        if (stale || overCap)
        {
            Interlocked.Add(ref _liveBytes[index], -estimate);
            contract.Release();
            if (profile.State != IlCodeProfile.LiveQueued)
                return;
            if (overCap)
            {
                Interlocked.Increment(ref _deferred);
                IlEvmSettings.CountDeferred();
                profile.State = IlCodeProfile.LiveDemoted;
                profile.CooldownUntil = Epoch(block) + source.CooldownEpochs;
            }
            else
            {
                profile.State = IlCodeProfile.LiveNone;
            }

            return;
        }

        contract.Profile = profile;
        contract.Origin = block ? IlOrigin.LiveBlocks : IlOrigin.LiveRpc;
        contract.RunMask = block ? (byte)((IlRuntime.RunMain | IlRuntime.RunWorkers) & IlRuntime.BlockThreadsMask | IlRuntime.RunQuery) : IlRuntime.RunQuery;
        contract.Check.Name = contract.Name;
        Volatile.Write(ref contract.BudgetBytes, contract.MemoryBytes);
        Interlocked.Add(ref _liveBytes[index], contract.MemoryBytes - estimate);
        Interlocked.Increment(ref _liveCount[index]);
        IlCheck.Arm(contract);
        profile.Live = contract;
        profile.State = IlCodeProfile.LivePromoted;
        Interlocked.Increment(ref _promoted);
        IlRuntime.Grew(block: block, query: true);
    }

    /// <summary>A live contract was released, by a demotion or by its check: the budget lets go of its bytes, once.</summary>
    internal static void Released(IlEvm.IlContract contract)
    {
        long bytes = Interlocked.Exchange(ref contract.BudgetBytes, 0);
        if (bytes == 0)
            return;
        int index = contract.Origin == IlOrigin.LiveBlocks ? 1 : 0;
        Interlocked.Add(ref _liveBytes[index], -bytes);
        Interlocked.Decrement(ref _liveCount[index]);
        Interlocked.Increment(ref _released);
    }

    /// <summary>Near the table's cap, scans the profiles in slices over the ticks, then evicts idle ones, the fewest decayed opcodes first.</summary>
    private static void EvictStep(int epoch, IlLiveParams source, long started, long budget)
    {
        int cap = IlRuntime.MaxProfiles;
        if (_scan is null)
        {
            if (IlRuntime.ProfileCount < cap - cap / 16)
                return;
            _scan = IlRuntime.Profiles.GetEnumerator();
            _evictable = [];
        }

        for (int seen = 0; seen < MinScan || Stopwatch.GetTimestamp() - started < budget; seen++)
        {
            if (!_scan.MoveNext())
            {
                Evict(epoch, cap);
                return;
            }

            IlCodeProfile profile = _scan.Current.Value;
            if (Evictable(profile, epoch))
                _evictable!.Add((OpsOf(profile, epoch, source), profile));
        }
    }

    private static double OpsOf(IlCodeProfile profile, int epoch, IlLiveParams source)
    {
        if (profile.Hist is not { } history)
            return 0;
        double ops = 0;
        for (int i = 1; i < PartsIndex; i += 2)
            ops += history[i];
        return ops * Keep(epoch - profile.HistEpoch, source);
    }

    private static bool Evictable(IlCodeProfile profile, int epoch) =>
        profile.Live is null && profile.Shipped is null && profile.ShippedNext is null && !profile.InLive && !profile.InCandidates &&
        profile.State is IlCodeProfile.LiveNone or IlCodeProfile.LiveDemoted && epoch - Math.Max(profile.TouchedEpoch, profile.CreatedEpoch) >= UntouchedEpochs;

    private static void Evict(int epoch, int cap)
    {
        List<(double Ops, IlCodeProfile Profile)> evictable = _evictable!;
        _scan?.Dispose();
        _scan = null;
        _evictable = null;
        int excess = IlRuntime.ProfileCount - cap * 7 / 8;
        if (excess <= 0)
            return;
        evictable.Sort(static (x, y) => x.Ops.CompareTo(y.Ops));
        for (int i = 0; i < evictable.Count && excess > 0; i++)
        {
            IlCodeProfile profile = evictable[i].Profile;
            // Looked at again: the scan took a while, and a frame may have counted it since.
            if (!Evictable(profile, epoch) || !IlRuntime.Evict(profile))
                continue;
            profile.State = IlCodeProfile.LiveEvicted;
            _evicted++;
            excess--;
        }
    }

    /// <summary>Takes the model's constants and the scorer's thresholds from a list's calibration lines and the environment, over the priors.</summary>
    /// <remarks>
    /// Model tokens as <see cref="IlModel.From"/> reads them, the queries' after <c>rpc.</c>; <c>NETHERMIND_ILEVM_LIVE_CALIB</c>
    /// adds tokens, which win; thresholds from <c>NETHERMIND_ILEVM_LIVE_{BLOCKS,RPC}_{THETA_NS,MIN_EPOCHS,MIN_MEASURED,HALF_LIFE}</c>.
    /// </remarks>
    public static void Calibrate(IReadOnlyList<string> calibration)
    {
        Blocks = Overridden(IlLiveParams.BlocksPrior, IlModel.BlocksPrior, calibration);
        Rpc = Overridden(IlLiveParams.RpcPrior, IlModel.RpcPrior, calibration);
    }

    private static IlLiveParams Overridden(IlLiveParams prior, IlModel model, IReadOnlyList<string> calibration)
    {
        List<string> lines = [.. calibration];
        if (Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_LIVE_CALIB") is { Length: > 0 } extra)
            lines.Add(extra);
        Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in IlEvmPrecompileList.CalibrationValues(lines))
            tokens[key] = value;
        string name = prior.Block ? "BLOCKS" : "RPC";
        return prior.With(
            minEpochs: (int?)Knob(name, "MIN_EPOCHS"),
            minMeasured: (int?)Knob(name, "MIN_MEASURED"),
            thetaUp: Knob(name, "THETA_NS"),
            halfLife: Knob(name, "HALF_LIFE"),
            model: IlModel.From(model, tokens, prior.Block ? "" : "rpc."));

        static double? Knob(string source, string knob) =>
            double.TryParse(Environment.GetEnvironmentVariable($"NETHERMIND_ILEVM_LIVE_{source}_{knob}"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) && value >= 0
                ? value
                : null;
    }

    /// <summary>The source's live contracts published and not released, and the IL and tables they hold with those reserved for queued layouts.</summary>
    public static (long Count, long Bytes) Live(bool block) =>
        (Interlocked.Read(ref _liveCount[block ? 1 : 0]), Interlocked.Read(ref _liveBytes[block ? 1 : 0]));

    public static long Displacements => Interlocked.Read(ref _displaced);

    public static (long Promoted, long Demoted, long Released, long Failed, long Evicted) Totals() =>
        (Interlocked.Read(ref _promoted), Interlocked.Read(ref _demoted), Interlocked.Read(ref _released), Interlocked.Read(ref _failed), Interlocked.Read(ref _evicted));

    public static string Describe()
    {
        if (Active is not { } source)
            return $"live off (prefer {IlEvmSettings.Prefer})";
        (long live, long bytes) = Live(source.Block);
        (long promoted, long demoted, long released, long failed, long evicted) = Totals();
        return string.Create(CultureInfo.InvariantCulture,
            $"live {source.Name}: {promoted} promoted, {demoted} demoted, {_displaced} displaced for room, {released} released, {failed} layouts failed, {Interlocked.Read(ref _deferred)} deferred, {_forkMoves} moved by a fork; " +
            $"{live} live in {bytes / (double)(1 << 20):F1} of {BudgetBytes >> 20} MiB; {_candidates.Count} candidates, {_scored} scored over {_ticks} ticks, {_folds} folds, {Touched.Count} waiting; {evicted} profiles evicted");
    }

    internal static (IReadOnlyList<IlCodeProfile> Live, IReadOnlyList<IlCodeProfile> Candidates) ListsForTests() => (_live.ToArray(), _candidates.ToArray());

    internal static void ResetForTests()
    {
        while (Touched.TryDequeue(out IlCodeProfile? profile))
            Volatile.Write(ref profile.InTouched, 0);
        foreach (IlCodeProfile profile in _live)
        {
            profile.InLive = false;
            // Its bytes are no budget's any more: a later release takes nothing off.
            if (profile.Live is { } live)
                Interlocked.Exchange(ref live.BudgetBytes, 0);
        }

        foreach (IlCodeProfile profile in _candidates)
            profile.InCandidates = false;
        _live.Clear();
        _candidates.Clear();
        _allVisits[0] = _allVisits[1] = 0;
        _allEpoch = 0;
        _scan?.Dispose();
        _scan = null;
        _evictable = null;
        _liveBytes[0] = _liveBytes[1] = 0;
        _liveCount[0] = _liveCount[1] = 0;
        _promoted = _demoted = _displaced = _released = _failed = _deferred = _evicted = _folds = _scored = _ticks = _forkMoves = 0;
        Calibrate([]);
    }
}

/// <summary>Live profiling's side of a code profile: its counts, written racily by the counting runner's threads, and what only the scorer writes.</summary>
internal sealed partial class IlCodeProfile
{
    /// <summary>Further values of <see cref="State"/>: a promotion's layout queued, not yet published; and a profile the table let go of.</summary>
    public const byte LiveQueued = 3, LiveEvicted = 4;

    /// <summary>Per kind and bucket, weighted visits then weighted opcodes; then weighted parts, and measured visits. Racy adds.</summary>
    public long[]? Counters;

    /// <summary>The epoch of the first count, which the scorer's rates count from.</summary>
    public int FirstEpoch;

    /// <summary>The epoch of the latest count, and how many epochs the code was counted in; racy.</summary>
    public int LastEpoch = -1, Epochs;

    public int TouchedEpoch = -1;

    /// <summary>1 while the profile is on the touched queue, so it is there once however many epochs go by before a tick.</summary>
    internal int InTouched;

    /// <summary>The epoch the profile was made in by a counting runner, from which an untouched one counts as old.</summary>
    public int CreatedEpoch;

    // The scorer's own, on the scheduler worker that holds the maintenance.

    /// <summary>The counts folded so far, decayed to <see cref="HistEpoch"/>; and the counts as last folded.</summary>
    public double[]? Hist;

    public long[]? Folded;

    public int HistEpoch;

    /// <summary>Ticks running a live code scored under a quarter of the threshold, and was resolved on another body set than its contract's.</summary>
    public int BelowTicks, MovedTicks;

    /// <summary>The latest score, in nanoseconds per epoch, its noise, the measured visits behind it, decayed, and the score per MiB of footprint.</summary>
    public double Score, Sigma, Measures, Density;

    internal bool InLive, InCandidates;
}

internal sealed partial class IlThread
{
    partial void CountLive(IlCodeProfile profile, int ops, int parts, bool main, int weightLog2, bool block) =>
        IlLive.Count(profile, ops, parts, main, 1 << weightLog2, block);
}
