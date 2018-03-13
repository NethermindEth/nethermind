using Nethermind.Core.Crypto.ZkSnarks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto.ZkSnarks
{
    [TestFixture]
    public class ParametersTests
    {
        [Test]
        public void P_is_fine()
        {
            Assert.NotNull(Parameters.P);
        }
        
        [Test]
        public void R_is_fine()
        {
            Assert.NotNull(Parameters.R);
        }
        
        [Test]
        public void FpB_is_fine()
        {
            Assert.NotNull(Parameters.FpB);
        }
        
        [Test]
        public void Fp2B_is_fine()
        {
            Assert.NotNull(Parameters.Fp2B);
        }
        
        [Test]
        public void Twist_is_fine()
        {
            Assert.NotNull(Parameters.Twist);
        }
        
        [Test]
        public void PairingFinalExponentZ_is_fine()
        {
            Assert.NotNull(Parameters.PairingFinalExponentZ);
        }
        
        [Test]
        public void TwistMulByPx_is_ine()
        {
            Assert.NotNull(Parameters.TwistMulByPx);
        }
        
        [Test]
        public void TwistMulByPy_is_ine()
        {
            Assert.NotNull(Parameters.TwistMulByPy);
        }
    }
}