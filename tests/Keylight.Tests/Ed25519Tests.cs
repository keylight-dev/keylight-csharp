using Keylight.Crypto;
using Xunit;

public class Ed25519Tests {
  // RFC 8032 Ed25519 TEST 2 (1-byte message 0x72).
  static byte[] Hex(string h) {
    var b = new byte[h.Length/2];
    for (int i=0;i<b.Length;i++) b[i]=System.Convert.ToByte(h.Substring(i*2,2),16);
    return b;
  }
  [Fact]
  public void Verifies_rfc8032_test2() {
    var pk  = Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
    var msg = Hex("72");
    var sig = Hex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
    Assert.True(Ed25519.Verify(sig, msg, pk));
    msg[0] ^= 0x01;
    Assert.False(Ed25519.Verify(sig, msg, pk));
  }
}
