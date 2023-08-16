using System.Diagnostics;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Core.Verkle
{
    public struct Committer
    {
        private static readonly CRS Constants = CRS.Instance;

        public static Banderwagon Commit(FrE[] value)
        {
            return Banderwagon.MultiScalarMul(Constants.BasisG, value);
        }

        public static Banderwagon ScalarMul(FrE value, int index)
        {
            return Constants.BasisG[index] * value;
        }
    }

    public class Commitment
    {
        private FrE? _pointAsField;

        public byte[] ToBytes() => Point.ToBytes();

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

        public void AddPoint(Banderwagon point)
        {
            Point += point;
            _pointAsField = null;
            SetCommitmentToField();
        }

        public FrE UpdateCommitmentGetDelta(Banderwagon point)
        {
            FrE prevPointAsField = PointAsField;
            Point += point;
            _pointAsField = null;
            SetCommitmentToField();
            return PointAsField - prevPointAsField;
        }
    }
}
