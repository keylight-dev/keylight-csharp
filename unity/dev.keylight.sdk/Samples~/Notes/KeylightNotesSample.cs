#if UNITY_2021_3_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Keylight.Unity.Samples {
  /// <summary>
  /// Minimal MonoBehaviour demonstrating the Keylight SDK in Unity.
  ///
  /// <para>
  /// On button press it calls <see cref="KeylightClient.ActivateAsync"/> and:
  /// <list type="bullet">
  ///   <item>Shows the current <see cref="KeylightState"/> in a status label.</item>
  ///   <item>Gates a "Pro" UI panel on <see cref="KeylightClient.HasEntitlement"/>.</item>
  /// </list>
  /// </para>
  ///
  /// <para>
  /// <strong>Main-thread safety:</strong>
  /// <see cref="KeylightClient.HasEntitlement"/> and
  /// <see cref="KeylightClient.State"/> are <em>synchronous</em> reads —
  /// they only access the on-disk lease cache and never touch the network.
  /// They are safe to call from <c>Update()</c>, UI callbacks, and any
  /// code that runs on the Unity main thread.
  /// <br/>
  /// <see cref="KeylightClient.ActivateAsync"/> is async and uses
  /// <see cref="UnityWebRequestTransport"/> under the hood, which resumes on
  /// the Unity main thread automatically (no <c>Dispatcher</c> or
  /// <c>SynchronizationContext</c> marshaling needed).
  /// </para>
  ///
  /// <para>
  /// <strong>Wire-up:</strong> Attach this script to a GameObject. In the
  /// Inspector, assign the UI fields and fill in your tenant/product/SDK key
  /// + trusted public key. In a real project these would be read from a
  /// ScriptableObject or remote config rather than hardcoded here.
  /// </para>
  /// </summary>
  public sealed class KeylightNotesSample : MonoBehaviour {

    // ─── Inspector fields ────────────────────────────────────────────────────

    [Header("Keylight config — replace with your own values")]
    [Tooltip("Your Keylight tenant ID.")]
    [SerializeField] private string tenantId  = "YOUR_TENANT_ID";

    [Tooltip("Your Keylight product ID.")]
    [SerializeField] private string productId = "YOUR_PRODUCT_ID";

    [Tooltip("Your Keylight SDK key (read-only client key, not the secret).")]
    [SerializeField] private string sdkKey    = "YOUR_SDK_KEY";

    [Tooltip("Hex-encoded Ed25519 public key for lease verification.")]
    [SerializeField] private string trustedKeyHex = "YOUR_HEX_PUBKEY";

    [Tooltip("Key ID that matches the kid in your leases.")]
    [SerializeField] private string trustedKeyId  = "k1";

    [Header("UI references")]
    [Tooltip("Input field where the user types their license key.")]
    [SerializeField] private TMP_InputField keyInputField;

    [Tooltip("Button the user presses to activate.")]
    [SerializeField] private Button activateButton;

    [Tooltip("Status label — shows the current KeylightState + any error.")]
    [SerializeField] private TMP_Text statusLabel;

    [Tooltip("Pro-only panel, shown only when HasEntitlement(\"pro\") is true.")]
    [SerializeField] private GameObject proPanel;

    // ─── private state ───────────────────────────────────────────────────────

    private KeylightClient _client;
    private bool _activating;

    // ─── lifecycle ───────────────────────────────────────────────────────────

    private async void Start() {
      var config = KeylightConfig
        .Builder(tenantId, productId, sdkKey)
        .TrustedKeys(new Dictionary<string, string> { [trustedKeyId] = trustedKeyHex })
        .Build();

      _client = KeylightUnity.CreateClient(config);

      activateButton.onClick.AddListener(OnActivateClicked);

      // Check on launch: refreshes a stale cached lease if present, and sends
      // the keyless beacon for an unlicensed install. Go through
      // KeylightUnity rather than the client directly — it stops the background
      // keyless heartbeat, whose thread-pool ticks cannot touch
      // UnityWebRequest. A Unity build beacons at launch only.
      try {
        await KeylightUnity.CheckOnLaunchAsync(_client);
      } catch (Exception ex) {
        Debug.LogWarning($"[Keylight] CheckOnLaunchAsync: {ex.Message}");
      }

      RefreshUI();
    }

    // Update() is called every frame by Unity.
    // HasEntitlement and State are synchronous reads — they only access the
    // local lease cache and are safe to call here (or in any UI callback).
    private void Update() {
      if (proPanel != null)
        proPanel.SetActive(_client?.HasEntitlement("pro") ?? false);
    }

    // ─── button handler ──────────────────────────────────────────────────────

    private async void OnActivateClicked() {
      if (_activating) return;
      var key = keyInputField != null ? keyInputField.text.Trim() : "";
      if (string.IsNullOrEmpty(key)) {
        SetStatus("Please enter a license key.", Color.yellow);
        return;
      }

      _activating = true;
      activateButton.interactable = false;
      SetStatus("Activating…", Color.white);

      try {
        // ActivateAsync POSTs to api.keylight.dev via UnityWebRequestTransport.
        // UnityWebRequest resumes on the main thread, so no Dispatcher needed.
        await _client.ActivateAsync(key);

        RefreshUI();
      } catch (ActivationException ex) {
        SetStatus($"Activation failed (HTTP {ex.StatusCode}): {ex.Message}", Color.red);
        Debug.LogError($"[Keylight] ActivationException: {ex}");
      } catch (LeaseVerificationFailedException ex) {
        SetStatus("Lease verification failed — untrusted key.", Color.red);
        Debug.LogError($"[Keylight] LeaseVerificationFailedException: {ex}");
      } catch (Exception ex) {
        SetStatus($"Error: {ex.Message}", Color.red);
        Debug.LogError($"[Keylight] Unexpected error: {ex}");
      } finally {
        _activating = false;
        activateButton.interactable = true;
      }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <see cref="KeylightClient.State"/> and
    /// <see cref="KeylightClient.HasEntitlement"/> (both synchronous) and
    /// updates the status label and pro-panel visibility.
    /// </summary>
    private void RefreshUI() {
      if (_client == null) return;

      var state     = _client.State;
      var hasPro    = _client.HasEntitlement("pro");

      // Update status label
      var msg = state switch {
        KeylightState.Licensed => hasPro ? "Licensed (Pro)" : "Licensed",
        KeylightState.Trial    => "Trial",
        KeylightState.FreeTier => "Free tier",
        // A trusted lease the server could only issue as `fallback`: run
        // degraded rather than locking the user out.
        KeylightState.Limited  => "Limited (reduced features)",
        KeylightState.Expired  => "License expired",
        KeylightState.Invalid  => "No license",
        _                      => state.ToString()
      };
      SetStatus(msg, state == KeylightState.Licensed ? Color.green : Color.white);

      // Gate the Pro panel
      if (proPanel != null)
        proPanel.SetActive(hasPro);
    }

    private void SetStatus(string message, Color color) {
      if (statusLabel == null) return;
      statusLabel.text  = message;
      statusLabel.color = color;
    }
  }
}
#endif // UNITY_2021_3_OR_NEWER
