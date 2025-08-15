namespace Lantern.Discv5.WireProtocol.Session;

public interface IAesCrypto
{
    byte[] AesCtrEncrypt(byte[] maskingKey, byte[] maskingIv, byte[] header);

    byte[]? AesCtrDecrypt(byte[] maskingKey, byte[] maskingIv, byte[] maskedHeader);

    byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] ad);

    byte[]? AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] ad);
}