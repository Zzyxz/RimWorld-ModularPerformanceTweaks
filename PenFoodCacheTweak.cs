using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class PenFoodCacheTweak : Tweak
{
    public override string Id => "PenFoodCache";
    public override string Category => "CPT_CategoryAnimals";

    public record struct Key
    {
        public bool Valid;
        public int Pen, Race, LifeStage, Policy, Area, Flags;
    }

    private static PenFoodCacheTweak instance;
    private static readonly Dictionary<int, Dictionary<Key, int>> byMap = new();
    private static long checkedCount, skipped, remembered, cleared;

    protected override void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap)),
            prefix: new HarmonyMethod(typeof(PenFoodCacheTweak), nameof(SearchPrefix)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(PenFoodCacheTweak), nameof(SearchPostfix)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup)),
            postfix: new HarmonyMethod(typeof(PenFoodCacheTweak), nameof(SpawnSetupPostfix)));
        harmony.Patch(AccessTools.PropertySetter(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden)),
            postfix: new HarmonyMethod(typeof(PenFoodCacheTweak), nameof(ForbiddenPostfix)));
        instance = this;
    }

    public override string Stats() => "CPT_PenFoodCacheStats".Translate(checkedCount, skipped, remembered, cleared);

    public override void ResetStats()
    {
        checkedCount = skipped = remembered = cleared = 0;
    }

    public override void ClearGame()
    {
        byMap.Clear();
    }

    public override void DrawOptions(Listing_Standard list)
    {
        var settings = ColonyPerformanceTweaksMod.Settings;
        settings.penFoodRememberTicks = Mathf.RoundToInt(list.SliderLabeled(
            "CPT_PenFoodRememberTicks".Translate(settings.penFoodRememberTicks, (settings.penFoodRememberTicks / 60f).ToString("0.0")),
            settings.penFoodRememberTicks, 60f, 2500f, 0.5f, "CPT_PenFoodRememberTicksTip".Translate()) / 10f) * 10;
    }

    private static void ClearMap(Map map)
    {
        if (map != null && byMap.TryGetValue(map.uniqueID, out var entries) && entries.Count > 0)
        {
            entries.Clear();
            cleared++;
        }
    }

    private static Key MakeKey(Pawn getter, Pawn eater, bool desperate, FoodPreferability maxPref, bool allowPlant, bool allowDrug, bool allowCorpse,
        bool allowForbidden, bool forceScanWholeMap, bool ignoreReservations, FoodPreferability minPrefOverride, bool allowVenerated)
    {
        var key = default(Key);
        if (instance == null || !instance.On || getter == null || getter != eater || forceScanWholeMap || !getter.Spawned || !getter.IsAnimal || getter.Faction != Faction.OfPlayer)
        {
            return key;
        }
        if (getter.roping != null && getter.roping.IsRoped)
        {
            return key;
        }
        var pen = AnimalPenUtility.GetCurrentPenOf(getter, allowUnenclosedPens: false);
        if (pen == null || pen.PenState.ConnectedRegions.Count > 100)
        {
            return key;
        }
        var policy = eater.foodRestriction?.CurrentFoodPolicy;
        key.Valid = true;
        key.Pen = pen.parent.thingIDNumber;
        key.Race = eater.def.index;
        key.LifeStage = eater.ageTracker?.CurLifeStageIndex ?? 0;
        key.Policy = policy != null ? policy.id + 1 : 0;
        var area = getter.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
        key.Area = area != null ? area.ID + 1 : 0;
        key.Flags = (desperate ? 1 : 0) | (allowPlant ? 2 : 0) | (allowDrug ? 4 : 0) | (allowCorpse ? 8 : 0) | (allowForbidden ? 16 : 0)
            | (ignoreReservations ? 32 : 0) | (allowVenerated ? 64 : 0) | ((int)maxPref << 8) | ((int)minPrefOverride << 16);
        return key;
    }

    private static bool IsKnownEmpty(Map map, in Key key)
    {
        if (!byMap.TryGetValue(map.uniqueID, out var entries))
        {
            return false;
        }
        if (!entries.TryGetValue(key, out int until))
        {
            return false;
        }
        return Find.TickManager.TicksGame < until;
    }

    private static void RememberEmpty(Map map, in Key key)
    {
        if (!byMap.TryGetValue(map.uniqueID, out var entries))
        {
            entries = new Dictionary<Key, int>();
            byMap[map.uniqueID] = entries;
        }
        int now = Find.TickManager.TicksGame;
        if (entries.Count > 256)
        {
            var expired = new List<Key>();
            foreach (var kv in entries)
            {
                if (kv.Value <= now)
                {
                    expired.Add(kv.Key);
                }
            }
            foreach (var k in expired)
            {
                entries.Remove(k);
            }
        }
        entries[key] = now + ColonyPerformanceTweaksMod.Settings.penFoodRememberTicks;
        remembered++;
    }

    private static bool IsNewFood(Thing thing) => thing.def.IsNutritionGivingIngestible && thing is not Plant;

    public static bool SearchPrefix(Pawn getter, Pawn eater, bool desperate, ref ThingDef foodDef, FoodPreferability maxPref, bool allowPlant, bool allowDrug,
        bool allowCorpse, bool allowForbidden, bool forceScanWholeMap, bool ignoreReservations, FoodPreferability minPrefOverride, bool allowVenerated,
        ref Thing __result, out Key __state)
    {
        __state = MakeKey(getter, eater, desperate, maxPref, allowPlant, allowDrug, allowCorpse, allowForbidden, forceScanWholeMap,
            ignoreReservations, minPrefOverride, allowVenerated);
        if (!__state.Valid)
        {
            return true;
        }
        checkedCount++;
        if (!IsKnownEmpty(getter.Map, __state))
        {
            return true;
        }
        skipped++;
        __state.Valid = false;
        __result = null;
        foodDef = null;
        return false;
    }

    public static void SearchPostfix(Pawn getter, Thing __result, Key __state)
    {
        if (__state.Valid && __result == null && getter?.Map != null && !PenHasFoodFor(getter))
        {
            RememberEmpty(getter.Map, __state);
        }
    }

    private static bool PenHasFoodFor(Pawn getter)
    {
        var pen = AnimalPenUtility.GetCurrentPenOf(getter, allowUnenclosedPens: false);
        if (pen == null)
        {
            return true;
        }
        foreach (var region in pen.PenState.ConnectedRegions)
        {
            var things = region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].Spawned && getter.WillEat(things[i], getter))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void SpawnSetupPostfix(Thing __instance, Map map)
    {
        if (map != null && byMap.Count > 0 && IsNewFood(__instance))
        {
            ClearMap(map);
        }
    }

    public static void ForbiddenPostfix(CompForbiddable __instance, bool value)
    {
        var thing = __instance.parent;
        if (!value && byMap.Count > 0 && thing != null && thing.Spawned && IsNewFood(thing))
        {
            ClearMap(thing.Map);
        }
    }
}
