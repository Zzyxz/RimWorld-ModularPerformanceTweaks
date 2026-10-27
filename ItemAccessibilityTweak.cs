using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class ItemAccessibilityTweak : Tweak
{
    public override string Id => "ItemAccessibility";
    public override string Category => "CPT_CategoryQuests";

#if !NET
    public override string Unavailable => "CPT_Only17";

    protected override void Apply(Harmony harmony)
    {
    }
#else
    private static ItemAccessibilityTweak instance;
    private static Action<PlanetTile> buildCache;
    private static Func<List<Thing>> accessibleThings;
    private static Func<List<ThingDefCount>> possiblyAccessibleThings;
    private static Func<int> cacheFrame;
    private static Func<PlanetTile> cacheTile;

    private static readonly Dictionary<ThingDef, int> accessibleCount = new();
    private static readonly Dictionary<ThingDef, int> possiblyCount = new();
    private static int sumsFrame = -1, sumsTick = -1;
    private static PlanetTile sumsTile = PlanetTile.Invalid;
    private static long answered, rebuilt;

    protected override void Apply(Harmony harmony)
    {
        var utility = typeof(PlayerItemAccessibilityUtility);
        var cache = AccessTools.Method(utility, "CacheAccessibleThings", new[] { typeof(PlanetTile) }) ?? throw new MissingMethodException("CacheAccessibleThings");
        buildCache = AccessTools.MethodDelegate<Action<PlanetTile>>(cache);
        accessibleThings = StaticGetter<List<Thing>>(utility, "cachedAccessibleThings");
        possiblyAccessibleThings = StaticGetter<List<ThingDefCount>>(utility, "cachedPossiblyAccessibleThings");
        cacheFrame = StaticGetter<int>(utility, "cachedAccessibleThingsForFrame");
        cacheTile = StaticGetter<PlanetTile>(utility, "cachedAccessibleThingsForTile");
        harmony.Patch(AccessTools.Method(utility, nameof(PlayerItemAccessibilityUtility.Accessible)),
            prefix: new HarmonyMethod(typeof(ItemAccessibilityTweak), nameof(AccessiblePrefix)));
        harmony.Patch(AccessTools.Method(utility, nameof(PlayerItemAccessibilityUtility.PossiblyAccessible)),
            prefix: new HarmonyMethod(typeof(ItemAccessibilityTweak), nameof(PossiblyAccessiblePrefix)));
        instance = this;
    }

    private static Func<T> StaticGetter<T>(Type type, string name)
    {
        var field = AccessTools.Field(type, name) ?? throw new MissingFieldException(type.Name + "." + name);
        if (field.FieldType != typeof(T))
        {
            throw new MissingFieldException(type.Name + "." + name + " has another type");
        }
        return Expression.Lambda<Func<T>>(Expression.Field(null, field)).Compile();
    }

    public override string Stats() => "CPT_ItemAccessibilityStats".Translate(answered, rebuilt);

    public override void ResetStats()
    {
        answered = rebuilt = 0;
    }

    public override void ClearGame()
    {
        accessibleCount.Clear();
        possiblyCount.Clear();
        sumsFrame = -1;
        sumsTick = -1;
        sumsTile = PlanetTile.Invalid;
    }

    private static void EnsureSums(Map map)
    {
        buildCache(map.Tile);
        int frame = cacheFrame();
        var tile = cacheTile();
        int tick = Find.TickManager.TicksGame;
        if (frame == sumsFrame && tile == sumsTile && tick == sumsTick)
        {
            return;
        }
        sumsFrame = -1;
        rebuilt++;
        accessibleCount.Clear();
        possiblyCount.Clear();
        var things = accessibleThings();
        for (int i = 0; i < things.Count; i++)
        {
            var t = things[i];
            if (t?.def == null)
            {
                continue;
            }
            accessibleCount.TryGetValue(t.def, out int n);
            accessibleCount[t.def] = n + t.stackCount;
        }
        var possibly = possiblyAccessibleThings();
        for (int i = 0; i < possibly.Count; i++)
        {
            var c = possibly[i];
            if (c.ThingDef == null)
            {
                continue;
            }
            possiblyCount.TryGetValue(c.ThingDef, out int n);
            possiblyCount[c.ThingDef] = n + c.Count;
        }
        sumsFrame = frame;
        sumsTile = tile;
        sumsTick = tick;
    }

    private static int Sum(Dictionary<ThingDef, int> sums, ThingDef thing) => thing != null && sums.TryGetValue(thing, out int n) ? n : 0;

    public static bool AccessiblePrefix(ThingDef thing, int count, Map map, ref bool __result)
    {
        if (instance == null || !instance.On || map == null)
        {
            return true;
        }
        EnsureSums(map);
        answered++;
        __result = Sum(accessibleCount, thing) >= count;
        return false;
    }

    public static bool PossiblyAccessiblePrefix(ThingDef thing, int count, Map map, ref bool __result)
    {
        if (instance == null || !instance.On || map == null)
        {
            return true;
        }
        if (PlayerItemAccessibilityUtility.Accessible(thing, count, map))
        {
            __result = true;
            return false;
        }
        EnsureSums(map);
        answered++;
        __result = Sum(possiblyCount, thing) >= count;
        return false;
    }
#endif
}
