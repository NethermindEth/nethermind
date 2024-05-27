using System;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Core.Verkle
{
    public readonly struct Committer
    {
        private static readonly CRS Constants = CRS.Instance;

        private static Banderwagon[] Zeros = null!;

        static Committer()
        {
            Zeros = new Banderwagon[256];
            for (int i = 0; i < 256; i++)
            {
                Zeros[i] = Constants.BasisG[i] * FrE.Zero;
            }
        }

        public static Banderwagon Commit(FrE[] value)
        {
            return Banderwagon.MultiScalarMul(Constants.BasisG, value);
        }

        public static Banderwagon ScalarMul(in FrE value, int index)
        {
            if (value.IsZero)
            {
                // Should not make a difference, but it seems to make some difference
                return Zeros[index];
            }
            return Constants.BasisG[index] * value;
        }

        public static Banderwagon MultiScalarMul(Span<(FrE, int)> pair)
        {
            if (pair.Length == 1)
            {
                Banderwagon accumulator = Banderwagon.Identity;
                foreach ((FrE, int) valueTuple in pair)
                {
                    accumulator += ScalarMul(valueTuple.Item1, valueTuple.Item2);
                }
                return accumulator;
            }

            using var values = new ArrayPoolList<FrE>(pair.Length);
            using var wagons = new ArrayPoolList<Banderwagon>(pair.Length);
            foreach ((FrE, int) valueTuple in pair)
            {
                values.Add(valueTuple.Item1);
                wagons.Add(Constants.BasisG[valueTuple.Item2]);
            }

            return Banderwagon.MultiScalarMul(wagons.AsSpan(), values.AsSpan());
        }
    }

    public class Commitment
    {
        private FrE? _pointAsField;

        public Hash256 ToBytes() => new Hash256(Point.ToBytes());

        public Commitment(Banderwagon point)
        {
            Point = point;
        }

        public Commitment()
        {
            Point = Banderwagon.Identity;
        }
        public Banderwagon Point { get; private set; }
        public FrE PointAsField
        {
            get
            {
                if (_pointAsField is null) SetCommitmentToField();
                Debug.Assert(_pointAsField is not null, nameof(_pointAsField) + " != null");
                return _pointAsField.Value;
            }
            private set => _pointAsField = value;
        }

        public Commitment Dup()
        {
            return new Commitment(Point);
        }

        private void SetCommitmentToField()
        {
            PointAsField = Point.MapToScalarField();
        }

        public void AddPoint(in Banderwagon point)
        {
            Point += point;
            _pointAsField = null;
            SetCommitmentToField();
        }

        public FrE UpdateCommitmentGetDelta(in Banderwagon point)
        {
            FrE prevPointAsField = PointAsField;
            Point += point;
            _pointAsField = null;
            SetCommitmentToField();
            return PointAsField - prevPointAsField;
        }
    }
}
