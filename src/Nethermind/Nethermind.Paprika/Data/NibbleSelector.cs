using System.Runtime.CompilerServices;

namespace Paprika.Data;

public interface INibbleSelector
{
    static abstract bool Should(byte nibble);
}

/// <summary>
/// A selector that has a defined super set. Yes, there can be many but some of them are more useful than others.
/// </summary>
/// <typeparam name="TSuperSetSelector"></typeparam>
public interface INibbleSelector<TSuperSetSelector> : INibbleSelector
    where TSuperSetSelector : INibbleSelector
{
    /// <summary>
    /// Whether the selector is low or high.
    /// </summary>
    static abstract bool Low { get; }
}

public static class NibbleSelector
{
    private const byte Count = 16;
    private const byte Forth = 4;

    private const byte _0_4 = 0;
    private const byte _1_4 = Count / Forth;
    private const byte _2_4 = 2 * Count / Forth;
    private const byte _3_4 = 3 * Count / Forth;
    private const byte _4_4 = 4 * Count / Forth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Between(byte nibble, byte low, byte high) => nibble >= low && nibble < high;

    public readonly struct All : INibbleSelector
    {
        public static bool Should(byte nibble) => true;
    }

    public readonly struct HalfLow : INibbleSelector<All>
    {
        public static bool Should(byte nibble) => Between(nibble, _0_4, _2_4);

        public static bool Low => true;
    }

    public readonly struct Q0 : INibbleSelector<HalfLow>
    {
        public static bool Should(byte nibble) => Between(nibble, _0_4, _1_4);

        public static bool Low => true;
    }

    public readonly struct Q1 : INibbleSelector<HalfLow>
    {
        public static bool Should(byte nibble) => Between(nibble, _1_4, _2_4);

        public static bool Low => false;
    }

    public readonly struct HalfHigh : INibbleSelector<All>
    {
        public static bool Should(byte nibble) => Between(nibble, _2_4, _4_4);

        public static bool Low => false;
    }

    public readonly struct Q2 : INibbleSelector<HalfHigh>
    {
        public static bool Should(byte nibble) => Between(nibble, _2_4, _3_4);

        public static bool Low => true;
    }

    public readonly struct Q3 : INibbleSelector<HalfHigh>
    {
        public static bool Should(byte nibble) => Between(nibble, _3_4, _4_4);

        public static bool Low => false;
    }
}
