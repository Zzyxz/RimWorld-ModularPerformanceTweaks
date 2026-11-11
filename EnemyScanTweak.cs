using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class EnemyScanTweak : Tweak
{
    public override string Id => "EnemyScan";
    public override string Category => "CPT_CategoryAnimals";

#if !NET
    public override string Unavailable => "CPT_Only17";

    protected override void Apply(Harmony harmony)
    {
    }
#else
    private const string OwnId = "Zzyxzz.ColonyPerformanceTweaks";
    private const string MonitorId = "Zzyxzz.PerformanceMonitor";
    private const string ZoologyFilterType = "ZoologyMod.Patch_SmallPetThreatDisabled";
    private const string AnimalsLogicPredatorType = "AnimalsLogic.Patches.HostilePredators";
    private const string AnimalsLogicPredatorMethod = "HostileToThing_Postfix";

    private static EnemyScanTweak instance;
    private static MethodBase target, hostile;
    private static bool conflictChecked, failed, inScan;
    private static string conflict;
    private static long scans, checks, shortcuts;

    private static Pawn scanPawn;
    private static IntVec3 scanPos;
    private static TraverseParms scanParms;
    private static float scanMaxDistance;
    private static int scanMaxCount, scanCount;
    private static bool scanInvisible, scanPlain;

    private static readonly RegionEntryPredicate Entry = (from, to) => to.Allows(scanParms, isDestination: false);
    private static readonly RegionProcessor Processor = ProcessRegion;

    protected override void Apply(Harmony harmony)
    {
        instance = this;
        target = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.EnemiesAreNearby)) ?? throw new MissingMethodException("PawnUtility.EnemiesAreNearby");
        var names = target.GetParameters().Select(p => p.Name).ToArray();
        if (!names.SequenceEqual(new[] { "pawn", "regionsToScan", "passDoors", "maxDistance", "maxCount", "invisible" }))
        {
            throw new MissingMethodException("PawnUtility.EnemiesAreNearby has another signature");
        }
        hostile = AccessTools.Method(typeof(GenHostility), nameof(GenHostility.HostileTo), new[] { typeof(Thing), typeof(Thing) })
            ?? throw new MissingMethodException("GenHostility.HostileTo(Thing, Thing)");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(EnemyScanTweak), nameof(Prefix)) { priority = Priority.Last });
    }

    private static void CheckConflict()
    {
        conflictChecked = true;
        var info = Harmony.GetPatchInfo(target);
        if (info != null)
        {
            var other = info.Transpilers
                .Concat(info.Prefixes.Where(ChangesResult))
                .FirstOrDefault(p => p.owner != OwnId && !IsMonitor(p));
            if (other != null)
            {
                conflict = "PawnUtility.EnemiesAreNearby, " + other.owner;
                return;
            }
        }
        info = Harmony.GetPatchInfo(hostile);
        if (info != null)
        {
            var other = info.Prefixes.Concat(info.Finalizers)
                .Concat(info.Postfixes.Where(p => !IsKnown(p, AnimalsLogicPredatorType, AnimalsLogicPredatorMethod)))
                .Concat(info.Transpilers.Where(p => p.PatchMethod?.DeclaringType?.FullName?.StartsWith(ZoologyFilterType, StringComparison.Ordinal) != true))
                .FirstOrDefault(p => p.owner != OwnId && !IsMonitor(p));
            if (other != null)
            {
                conflict = "GenHostility.HostileTo, " + other.owner;
            }
        }
    }

    private static bool IsKnown(Patch patch, string typeName, string methodName) =>
        patch.PatchMethod?.DeclaringType?.FullName == typeName && patch.PatchMethod.Name == methodName;

    private static bool IsMonitor(Patch patch) => patch.owner != null && patch.owner.StartsWith(MonitorId, StringComparison.Ordinal);

    private static bool ChangesResult(Patch patch)
    {
        var method = patch.PatchMethod;
        if (method == null)
        {
            return false;
        }
        if (method.ReturnType == typeof(bool))
        {
            return true;
        }
        foreach (var parameter in method.GetParameters())
        {
            string name = parameter.Name ?? "";
            if (name == "__runOriginal" || name == "__result" || (parameter.ParameterType.IsByRef && !name.StartsWith("__", StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    public override string Stats() => conflict != null
        ? "CPT_EnemyScanConflict".Translate(conflict).ToString()
        : failed ? "CPT_EnemyScanFailed".Translate().ToString() : "CPT_EnemyScanStats".Translate(scans, checks, shortcuts).ToString();

    public override void ResetStats()
    {
        scans = checks = shortcuts = 0;
    }

    public override void ClearGame()
    {
        failed = false;
    }

    public static bool Prefix(Pawn pawn, int regionsToScan, bool passDoors, float maxDistance, int maxCount, bool invisible, ref bool __result)
    {
        if (instance == null || !instance.On || failed || inScan || pawn?.Map == null || !UnityData.IsInMainThread)
        {
            return true;
        }
        if (!conflictChecked)
        {
            CheckConflict();
        }
        if (conflict != null)
        {
            return true;
        }
        inScan = true;
        try
        {
            scanPawn = pawn;
            scanPos = pawn.Position;
            scanParms = passDoors ? TraverseParms.For(TraverseMode.PassDoors) : TraverseParms.For(pawn);
            scanMaxDistance = maxDistance;
            scanMaxCount = maxCount;
            scanInvisible = invisible;
            scanCount = 0;
            scanPlain = Plain(pawn);
            RegionTraverser.BreadthFirstTraverse(scanPos, pawn.Map, Entry, Processor, regionsToScan);
            __result = scanCount >= maxCount;
            scans++;
            return false;
        }
        catch (Exception e)
        {
            failed = true;
            Log.Warning("[Modular Performance Tweaks] Quicker enemy check switched off after an error, the game's own code is used: " + e);
            return true;
        }
        finally
        {
            inScan = false;
            scanPawn = null;
        }
    }

    private static bool ProcessRegion(Region r)
    {
        var list = r.ListerThings.ThingsInGroup(ThingRequestGroup.AttackTarget);
        for (int i = 0; i < list.Count; i++)
        {
            Thing thing = list[i];
            if ((scanMaxDistance <= 0f || thing.Position.InHorDistOf(scanPos, scanMaxDistance)) && Hostile(thing)
                && (scanInvisible || !(thing is Pawn other) || !other.IsPsychologicallyInvisible()))
            {
                scanCount++;
            }
        }
        return scanCount >= scanMaxCount;
    }

    private static bool Hostile(Thing thing)
    {
        checks++;
        if (scanPlain && thing is Pawn other && other != scanPawn && CannotBeHostile(other, scanPawn))
        {
            shortcuts++;
            return false;
        }
        return thing.HostileTo(scanPawn);
    }

    private static bool Plain(Pawn p)
    {
        var race = p.RaceProps;
        if (race == null || race.Humanlike || p.kindDef == null || p.kindDef.hostileToAll || p.guest != null || p.MentalState != null)
        {
            return false;
        }
        if (p.CurJobDef == JobDefOf.PredatorHunt)
        {
            return false;
        }
        if (p.Faction == null && p.GetComp<CompCauseGameCondition>() != null)
        {
            return false;
        }
        return !p.IsShambler;
    }

    private static bool CannotBeHostile(Pawn a, Pawn b)
    {
        var fa = a.Faction;
        var fb = b.Faction;
        if (fa != null && fb != null && fa != fb)
        {
            return false;
        }
        if (!Plain(a))
        {
            return false;
        }
        if (fb != null && fb.HasPredatorRecentlyAttackedAnyone(a))
        {
            return false;
        }
        if (fa != null && fa.HasPredatorRecentlyAttackedAnyone(b))
        {
            return false;
        }
        return true;
    }
#endif
}
