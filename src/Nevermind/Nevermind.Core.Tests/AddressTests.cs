using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Tests
{
    [TestClass]
    public class AddressTests
    {
        [DataTestMethod]
        [DataRow("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        [DataRow("0x5a4eab120fb44eb6684e5e32785702ff45ea344d", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        public void String_representation_is_correct(string init, string expected)
        {
            Address address = new Address(init);
            string addressString = address.ToString();
            Assert.AreEqual(expected, addressString);
        }

        [DataTestMethod]
        [DataRow("0x52908400098527886E0F7030069857D2E4169EE7", "0x52908400098527886E0F7030069857D2E4169EE7")]
        [DataRow("0x8617E340B3D01FA5F11F306F4090FD50E238070D", "0x8617E340B3D01FA5F11F306F4090FD50E238070D")]
        [DataRow("0xde709f2102306220921060314715629080e2fb77", "0xde709f2102306220921060314715629080e2fb77")]
        [DataRow("0x27b1fdb04752bbc536007a920d24acb045561c26", "0x27b1fdb04752bbc536007a920d24acb045561c26")]
        [DataRow("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
        [DataRow("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
        [DataRow("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB", "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
        [DataRow("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb", "0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
        [DataRow("0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741", "0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741")]
        [DataRow("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5A4EAB120fB44eb6684E5e32785702FF45ea344D")]
        [DataRow("0xa7dD84573f5ffF821baf2205745f768F8edCDD58", "0xa7dD84573f5ffF821baf2205745f768F8edCDD58")]
        [DataRow("0x027a49d11d118c0060746F1990273FcB8c2fC196", "0x027a49d11d118c0060746F1990273FcB8c2fC196")]
        public void String_representation_with_checksum_is_correct(string init, string expected)
        {
            Address address = new Address(init);
            string addressString = address.ToString(true);
            Assert.AreEqual(expected, addressString);
        }
    }
}
