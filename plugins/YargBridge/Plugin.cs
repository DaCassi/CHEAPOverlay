using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using YARG.Gameplay.Player;

// =============================================================================
// CHEAPOverlay — YARG Bridge (BepInEx 5.x plugin)
//
// Hooks BasePlayer.OnStarPowerStatus(bool active) — the protected virtual
// method that fires on every star power state change for every instrument
// type (guitar, bass, drums, keys, vocals).
//
// Sends a small UDP datagram to CHEAPOverlay's listener on localhost:2829:
//   {"player":<0-3>,"sp":<true|false>}
// The player index comes from BasePlayer.HighwayIndex (0-3), which maps
// directly to CHEAPOverlay's PlayerStates slots.
//
// Install: copy CHEAPOverlay.YargBridge.dll to <YARG>\BepInEx\plugins\
// Build:   dotnet build -p:YargDir="<path to YARG install>"
// =============================================================================

namespace CHEAPOverlay.YargBridge
{
    [BepInPlugin("com.cheapoverlay.yargbridge", "CHEAPOverlay YARG Bridge", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static UdpClient?      Udp;
        internal static readonly IPEndPoint OverlayEndpoint = new(IPAddress.Loopback, 2829);

        void Awake()
        {
            Log = Logger;
            try
            {
                Udp = new UdpClient();
                Log.LogInfo($"CHEAPOverlay UDP sender ready → {OverlayEndpoint}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"CHEAPOverlay UDP init failed: {ex.Message}");
            }

            new Harmony("com.cheapoverlay.yargbridge").PatchAll();
            Log.LogInfo("CHEAPOverlay YARG Bridge loaded.");
        }

        void OnDestroy()
        {
            Udp?.Close();
            Udp = null;
        }
    }

    [HarmonyPatch(typeof(BasePlayer), "OnStarPowerStatus")]
    static class StarPowerPatch
    {
        // Fires for every instrument type — guitar, bass, drums, keys, vocals.
        // __instance.HighwayIndex is the 0-based player slot set by Initialize().
        static void Postfix(BasePlayer __instance, bool active)
        {
            if (Plugin.Udp == null) return;
            int  slot = __instance.HighwayIndex;
            var  msg  = Encoding.UTF8.GetBytes(
                $"{{\"player\":{slot},\"sp\":{(active ? "true" : "false")}}}");
            try { Plugin.Udp.Send(msg, msg.Length, Plugin.OverlayEndpoint); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CHEAPOverlay send failed: {ex.Message}");
            }
        }
    }
}
