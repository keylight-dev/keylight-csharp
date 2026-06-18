using System.Collections.Generic;
using Keylight;
using Xunit;

public class KeysetTests {
  private const string ValidJson = @"{
    ""primary_kid"": ""k1"",
    ""keys"": [
      { ""kid"": ""k1"", ""alg"": ""ed25519"", ""public_key"": ""dGVzdGtleWJhc2U2NA=="" }
    ]
  }";

  [Fact]
  public void Parse_valid_keyset_returns_primary_kid_and_key_map() {
    var keyset = Keyset.Parse(ValidJson);
    Assert.NotNull(keyset);
    Assert.Equal("k1", keyset!.PrimaryKid);
    Assert.True(keyset.Keys.ContainsKey("k1"));
    Assert.Equal("dGVzdGtleWJhc2U2NA==", keyset.Keys["k1"]);
  }

  [Fact]
  public void Parse_malformed_entry_missing_public_key_returns_null() {
    // A key element missing public_key → return null (do NOT skip, do NOT poison)
    var json = @"{
      ""primary_kid"": ""k1"",
      ""keys"": [
        { ""kid"": ""k1"" }
      ]
    }";
    Assert.Null(Keyset.Parse(json));
  }

  [Fact]
  public void Parse_malformed_entry_missing_kid_returns_null() {
    var json = @"{
      ""primary_kid"": ""k1"",
      ""keys"": [
        { ""public_key"": ""dGVzdA=="" }
      ]
    }";
    Assert.Null(Keyset.Parse(json));
  }

  [Fact]
  public void Parse_missing_primary_kid_returns_null() {
    var json = @"{ ""keys"": [ { ""kid"": ""k1"", ""public_key"": ""dGVzdA=="" } ] }";
    Assert.Null(Keyset.Parse(json));
  }

  [Fact]
  public void Parse_keys_not_array_returns_null() {
    var json = @"{ ""primary_kid"": ""k1"", ""keys"": ""not_an_array"" }";
    Assert.Null(Keyset.Parse(json));
  }

  [Fact]
  public void Parse_empty_string_returns_null() {
    Assert.Null(Keyset.Parse(""));
  }

  [Fact]
  public void Parse_garbage_string_returns_null() {
    Assert.Null(Keyset.Parse("not json at all %%%"));
  }

  [Fact]
  public void Parse_multiple_keys_builds_full_map() {
    var json = @"{
      ""primary_kid"": ""k2"",
      ""keys"": [
        { ""kid"": ""k1"", ""alg"": ""ed25519"", ""public_key"": ""a2V5MQ==""},
        { ""kid"": ""k2"", ""alg"": ""ed25519"", ""public_key"": ""a2V5Mg==""}
      ]
    }";
    var keyset = Keyset.Parse(json);
    Assert.NotNull(keyset);
    Assert.Equal("k2", keyset!.PrimaryKid);
    Assert.Equal(2, keyset.Keys.Count);
    Assert.Equal("a2V5MQ==", keyset.Keys["k1"]);
    Assert.Equal("a2V5Mg==", keyset.Keys["k2"]);
  }
}
