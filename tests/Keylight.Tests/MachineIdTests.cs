using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class MachineIdTests {
    [Fact]
    public void Canonical_vector_matches_every_other_sdk() {
      // Pinned in keylight-rust machine.rs and keylight-cpp test_free_tier.cpp.
      Assert.Equal(
        "8e8871112f28cabda180ada131d0b4f4f07c72fb47c5d884edbe32812885b22a",
        MachineId.Hash("testco", "testapp", "hardware-1"));
    }

    [Fact]
    public void Hash_is_lowercase_hex_of_64_chars() {
      var h = MachineId.Hash("t", "p", "x");
      Assert.Equal(64, h.Length);
      Assert.Matches("^[a-f0-9]{64}$", h);
    }
  }
}
