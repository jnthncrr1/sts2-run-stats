using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace RunStats;

/// <summary>Mounts the overlay once the game's root node is up, and toggles it on F2.</summary>
[HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
internal static class OverlayMountPatch
{
    private static void Postfix()
    {
        try { StatsOverlay.EnsureMounted(); }
        catch (Exception e) { GD.PushWarning($"[RunStats] overlay mount failed: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class OverlayHotkeyPatch
{
    private static void Postfix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
            StatsOverlay.ToggleHidden();
    }
}
