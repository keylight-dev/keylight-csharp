using System.Collections.Generic;
using Keylight;
using Keylight.Tests;
using Xunit;

public class ConformanceTests {
  public class Expect { public bool kidKnown { get; set; } public bool signatureValid { get; set; } public bool expired { get; set; } }
  public class VecWithExpect {
    public string name { get; set; } = "";
    public Lease lease { get; set; } = new Lease();
    public Dictionary<string, string> trustedKeys { get; set; } = new();
    public long now { get; set; }
    public Expect expect { get; set; } = new Expect();
  }
  public class ConformanceFile {
    public int skewSeconds { get; set; }
    public List<VecWithExpect> vectors { get; set; } = new();
  }

  public static IEnumerable<object[]> Vectors() {
    var json = System.IO.File.ReadAllText(
      System.IO.Path.Combine(System.AppContext.BaseDirectory, "conformance", "vectors.json"));
    var f = System.Text.Json.JsonSerializer.Deserialize<ConformanceFile>(json,
      new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    foreach (var v in f.vectors) yield return new object[] { v.name, v, f.skewSeconds };
  }

  [Theory]
  [MemberData(nameof(Vectors))]
  public void Vector_matches_expect(string _, VecWithExpect v, int skew) {
    var r = Verifier.VerifyLease(v.lease, v.trustedKeys, v.now, skew);
    Assert.Equal(v.expect.kidKnown, r.KidKnown);
    Assert.Equal(v.expect.signatureValid, r.SignatureValid);
    Assert.Equal(v.expect.expired, r.Expired);
  }
}
