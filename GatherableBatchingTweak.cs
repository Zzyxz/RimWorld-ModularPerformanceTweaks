using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class GatherableBatchingTweak : Tweak
{
    public override string Id => "GatherableBatching";
    public override string Category => "CPT_CategoryAnimals";

    private const int Interval = 60;

    private static GatherableBatchingTweak instance;
    private static Func<CompHasGatherableBodyResource, bool> isActive;
    private static Func<CompHasGatherableBodyResource, int> intervalDays;
    private static AccessTools.FieldRef<CompHasGatherableBodyResource, float> fullness;

    protected override void Apply(Harmony harmony)
    {
        isActive = AccessTools.MethodDelegate<Func<CompHasGatherableBodyResource, bool>>(
            AccessTools.PropertyGetter(typeof(CompHasGatherableBodyResource), "Active"), virtualCall: true);
        intervalDays = AccessTools.MethodDelegate<Func<CompHasGatherableBodyResource, int>>(
            AccessTools.PropertyGetter(typeof(CompHasGatherableBodyResource), "GatherResourcesIntervalDays"), virtualCall: true);
        fullness = AccessTools.FieldRefAccess<CompHasGatherableBodyResource, float>("fullness");
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CompHasGatherableBodyResource), nameof(CompHasGatherableBodyResource.CompTick)),
            prefix: new HarmonyMethod(typeof(GatherableBatchingTweak), nameof(Prefix)));
        instance = this;
    }

    public static bool Prefix(CompHasGatherableBodyResource __instance)
    {
        if (!instance.On || __instance.parent is not Pawn pawn || !pawn.Spawned)
        {
            return true;
        }
        if ((Find.TickManager.TicksGame + pawn.thingIDNumber) % Interval != 0)
        {
            return false;
        }
        if (!isActive(__instance))
        {
            return false;
        }
        int days = intervalDays(__instance);
        if (days <= 0)
        {
            return true;
        }
        ref float full = ref fullness(__instance);
        full += Interval / (float)(days * 60000) * PawnUtility.BodyResourceGrowthSpeed(pawn);
        if (full > 1f)
        {
            full = 1f;
        }
        return false;
    }
}
