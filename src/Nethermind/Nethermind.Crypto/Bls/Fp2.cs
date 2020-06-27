using System.Runtime.InteropServices;

namespace Nethermind.Crypto.Bls
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Fp2
    {
        private readonly Fp _fp0;
        private readonly Fp _fp1;

        public Fp2(Fp fp0, Fp fp1)
        {
            _fp0 = fp0;
            _fp1 = fp1;
        }

        public G2 MapToG2()
        {
            G2 g2 = new G2();
            MclBls12.mclBnFp2_mapToG2(ref g2, ref this);
            return g2;
        }
    }
}