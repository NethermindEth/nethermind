using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paprika.Utils;

public static class SpanExtensions
{
    private static readonly byte[] ZeroByte = [0];

    public static Span<byte> WithoutLeadingZeros(this Span<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return ZeroByte;
        }

        int nonZeroIndex = bytes.IndexOfAnyExcept((byte)0);

        return nonZeroIndex < 0 ? bytes[^1..] : bytes[nonZeroIndex..];
    }

    /// <summary>
    /// Batches consecutive numbers into ranges.
    /// </summary>
    public static RangeEnumerator<T> BatchConsecutive<T>(this Span<T> span, int maxBatchLength = int.MaxValue)
        where T : INumberBase<T>
    {
        return new RangeEnumerator<T>(span, maxBatchLength);
    }

    public ref struct RangeEnumerator<T>
        where T : INumberBase<T>
    {
        public RangeEnumerator<T> GetEnumerator() => this;

        /// <summary>The span being enumerated.</summary>
        private readonly Span<T> _span;

        private readonly int _maxRangeLength;

        /// <summary>The start of the current range.</summary>
        private int _from;

        private int _length;

        /// <summary>Initialize the enumerator.</summary>
        /// <param name="span">The span to enumerate.</param>
        /// <param name="maxRangeLength">The maximum batch length.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal RangeEnumerator(Span<T> span, int maxRangeLength)
        {
            _span = span;
            _maxRangeLength = maxRangeLength;
            _from = -1;
            _length = 1;
        }

        /// <summary>Advances the enumerator to the next element of the span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            _from += _length;
            _length = 1;

            if (_from >= _span.Length)
                return false;

            for (var i = _from + _length; i < _span.Length; i++)
            {
                var next = _span[i - 1] + T.One;
                if (next == _span[i] && _length < _maxRangeLength)
                {
                    _length += 1;
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public (T Start, int Length) Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(_span[_from], _length);
        }
    }
}
