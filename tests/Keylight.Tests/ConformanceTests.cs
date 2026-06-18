using System.Collections.Generic; using System.IO; using System.Text.Json;
using Keylight; using Xunit;

public class ConformanceTests {
  public class Vec { public string name {get;set;} public Lease lease {get;set;}
    public Dictionary<string,string> trustedKeys {get;set;} public long now {get;set;}
    public Expect expect {get;set;} }
  public class Expect { public bool kidKnown {get;set;} public bool signatureValid {get;set;} public bool expired {get;set;} }
  public class File { public int skewSeconds {get;set;} public List<Vec> vectors {get;set;} }

  public static IEnumerable<object[]> Vectors() {
    var json = System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "vectors.json"));
    var f = JsonSerializer.Deserialize<File>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    foreach (var v in f.vectors) yield return new object[] { v.name, v, f.skewSeconds };
  }

  [Theory]
  [MemberData(nameof(Vectors))]
  public void Vector_matches_expect(string name, Vec v, int skew) {
    var r = Verifier.VerifyLease(v.lease, v.trustedKeys, v.now, skew);
    Assert.Equal(v.expect.kidKnown, r.KidKnown);
    Assert.Equal(v.expect.signatureValid, r.SignatureValid);
    Assert.Equal(v.expect.expired, r.Expired);
  }
}
