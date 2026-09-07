#if UNITY_2021_3_OR_NEWER
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Keylight.Unity {
  /// <summary>
  /// Convenience factory that wires up a <see cref="KeylightClient"/> for
  /// Unity: uses <see cref="UnityLeaseStore"/> for persistence and
  /// <see cref="UnityWebRequestTransport"/> so the package works on all Unity
  /// platforms — including <strong>WebGL</strong>, where <c>HttpClient</c> is
  /// unavailable.
  ///
  /// Usage (MonoBehaviour):
  /// <code>
  /// var client = KeylightUnity.CreateClient(
  ///   KeylightConfig.Builder("tenant", "product", "sdk-key")
  ///     .TrustedKeys(new Dictionary&lt;string, string&gt; { ["kid"] = "hex-pubkey" })
  ///     .Platform(Application.platform.ToString())
  ///     .Build());
  /// await KeylightUnity.CheckOnLaunchAsync(client);
  /// </code>
  ///
  /// Use <see cref="CheckOnLaunchAsync"/> rather than the client's own
  /// <c>CheckOnLaunchAsync</c> — see that method for why.
  /// </summary>
  public static class KeylightUnity {
    /// <summary>
    /// Creates a fully configured <see cref="KeylightClient"/> using:
    /// <list type="bullet">
    ///   <item><see cref="UnityLeaseStore"/> backed by <c>Application.persistentDataPath</c></item>
    ///   <item><see cref="UnityWebRequestTransport"/> — works on ALL platforms including WebGL</item>
    /// </list>
    /// To stamp a Unity-specific <c>platform</c> string on outbound requests
    /// (e.g. "WindowsPlayer", "IPhonePlayer", "WebGLPlayer"), call
    /// <c>.Platform(Application.platform.ToString())</c> on the
    /// <see cref="KeylightConfig.ConfigBuilder"/> before passing the config in.
    /// If omitted, the platform falls back to the runtime-detected value from
    /// <see cref="Device.Platform"/>.
    /// </summary>
    /// <param name="config">A fully built <see cref="KeylightConfig"/>.</param>
    /// <param name="leaseFilename">
    ///   Optional filename for the JSON lease file (default: <c>keylight-lease.json</c>).
    /// </param>
    public static KeylightClient CreateClient(
      KeylightConfig config,
      string leaseFilename = "keylight-lease.json") {

      // The core's physical-memory probe is compiled out under Unity: WebGL has
      // no native interop and iOS IL2CPP needs DllImport("__Internal"), so
      // keeping Runtime/Core free of native calls is worth more than a
      // telemetry field. SystemInfo is Unity's own cross-platform answer and
      // works on every build target, so feed it in here instead. It reports
      // whole megabytes; the core buckets the byte count.
      if (SystemInfo.systemMemorySize > 0)
        PhysicalMemory.SetTotalBytes((long)SystemInfo.systemMemorySize * 1024L * 1024L);

      // The core's native hardware-id probes are compiled out under Unity for
      // the same reason as the memory probe. SystemInfo.deviceUniqueIdentifier
      // is Unity's cross-platform stable device id; it is NOT the same source
      // the native SDKs read (IOPlatformUUID / MachineGuid), so a Unity build
      // and a C++ build of the same product on one machine hash differently.
      // It is still stable across reinstalls, which is what the dedupe needs.
      SystemDeviceIdentity.SetHardwareId(SystemInfo.deviceUniqueIdentifier);

      var store     = new UnityLeaseStore(leaseFilename);
      var transport = new UnityWebRequestTransport(config.BaseUrl, config.TenantId, config.ProductId, config.SdkKey);
      return new KeylightClient(config, store, transport);
    }

    /// <summary>
    /// Unity's launch check. Runs <see cref="KeylightClient.CheckOnLaunchAsync"/>
    /// and then stops the keyless heartbeat, so a Unity build beacons
    /// <b>at launch only</b>.
    /// </summary>
    /// <remarks>
    /// <para>Call this from a MonoBehaviour's <c>Start</c> instead of the
    /// client's own <c>CheckOnLaunchAsync</c>. The launch call itself runs on
    /// Unity's main thread, which is what <see cref="UnityWebRequestTransport"/>
    /// needs.</para>
    ///
    /// <para><b>Why the heartbeat is stopped.</b>
    /// <see cref="KeylightClient.StartKeylessHeartbeat"/> — which the launch
    /// check starts for you — schedules its ticks on a
    /// <c>System.Threading.Timer</c>, i.e. a thread-pool thread.
    /// <c>UnityWebRequest</c> is a main-thread-only object, so every tick would
    /// throw where it constructs one; the beacon swallows the exception, which
    /// makes it a silent no-op that still burns a timer thread. Stopping it is
    /// honest about what Unity actually does. A coroutine-driven heartbeat that
    /// can tick on the main thread is the follow-up.</para>
    /// </remarks>
    public static async Task CheckOnLaunchAsync(
      KeylightClient client,
      CancellationToken ct = default) {
      // No ConfigureAwait(false): the core awaits with it everywhere, so this
      // continuation is what hops back onto Unity's SynchronizationContext for
      // the caller.
      await client.CheckOnLaunchAsync(ct);
      client.StopKeylessHeartbeat();
    }
  }
}
#endif // UNITY_2021_3_OR_NEWER
