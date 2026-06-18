// Vendored from Chaos.NaCl (public domain): https://github.com/CodesInChaos/Chaos.NaCl
#nullable disable
namespace Keylight.Crypto
{
    /// <summary>Pure-managed Ed25519 verification (vendored Chaos.NaCl). No native deps — IL2CPP/WebGL safe.</summary>
    public static class Ed25519
    {
        /// <param name="publicKey">32-byte raw Ed25519 public key.</param>
        public static bool Verify(byte[] signature, byte[] message, byte[] publicKey)
        {
            if (signature == null || signature.Length != 64) return false;
            if (publicKey == null || publicKey.Length != 32) return false;
            if (message == null) return false;
            return Ed25519Operations.crypto_sign_verify(signature, 0, message, 0, message.Length, publicKey, 0);
        }
    }
}
