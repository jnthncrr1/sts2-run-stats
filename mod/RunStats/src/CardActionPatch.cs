using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace RunStats;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
public static class CardActionScopePatch
{
    [HarmonyPrefix]
    public static void Before(CardModel __instance, out object? __state) =>
        __state = ModEntry.Recorder?.EnterActionScope(__instance);

    [HarmonyPostfix]
    public static void After(object? __state, ref Task __result)
    {
        if (__state != null && ModEntry.Recorder != null)
            __result = ModEntry.Recorder.CompleteActionScope(__result, __state);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
public static class CardActionStartPatch
{
    [HarmonyPrefix]
    public static void Before(CardPlay cardPlay) => ModEntry.Recorder?.CardPlayed(cardPlay);
}
