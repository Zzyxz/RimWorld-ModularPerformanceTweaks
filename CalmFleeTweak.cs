using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyPerformanceTweaks;

public sealed class CalmFleeTweak : Tweak
{
    public override string Id => "CalmFlee";
    public override string Category => "CPT_CategoryAnimals";

    private const int QuietTicks = 250;
    private const int DangerRefreshTicks = 15;
    private const float NearDistance = 32f;

    private static CalmFleeTweak instance;
    private static long skipped, run, dangerNear, notEligible;
    private static int depth;

    private static readonly Dictionary<int, int> quietUntil = new();
    private static readonly Dictionary<int, DangerList> dangerByMap = new();
    private static System.Type carrierExtensionType;
    private static AccessTools.FieldRef<object, float> carrierRadius;

    private sealed class DangerList
    {
        public int until = -1;
        public readonly List<IntVec3> cells = new();
        public readonly List<float> radiusSquared = new();
    }

    protected override void Apply(Harmony harmony)
    {
        instance = this;
        var prefix = new HarmonyMethod(typeof(CalmFleeTweak), nameof(Prefix)) { priority = Priority.First };
        var postfix = new HarmonyMethod(typeof(CalmFleeTweak), nameof(Postfix)) { priority = Priority.Last };
        harmony.Patch(AccessTools.DeclaredMethod(typeof(JobGiver_AnimalFlee), "TryGiveJob"), prefix: prefix, postfix: postfix);
        var zoology = AccessTools.TypeByName("ZoologyMod.JobGiver_AnimalFlee_Zoology");
        var zoologyTryGiveJob = zoology != null ? AccessTools.DeclaredMethod(zoology, "TryGiveJob") : null;
        if (zoologyTryGiveJob != null)
        {
            harmony.Patch(zoologyTryGiveJob, prefix: prefix, postfix: postfix);
        }
        carrierExtensionType = AccessTools.TypeByName("ZoologyMod.ModExtension_FleeFromCarrier");
        if (carrierExtensionType != null && AccessTools.Field(carrierExtensionType, "fleeRadius") != null)
        {
            carrierRadius = AccessTools.FieldRefAccess<float>(carrierExtensionType, "fleeRadius");
        }
    }

    public override string Stats() => "CPT_CalmFleeStats".Translate(skipped, run, dangerNear, notEligible);

    public override void ResetStats()
    {
        skipped = run = dangerNear = notEligible = 0;
    }

    public override void ClearGame()
    {
        quietUntil.Clear();
        dangerByMap.Clear();
        depth = 0;
    }

    public static bool Prefix(Pawn pawn, ref Job __result, out bool __state)
    {
        __state = false;
        if (!instance.On || pawn == null)
        {
            return true;
        }
        int id = pawn.thingIDNumber;
        if (!IsEligible(pawn))
        {
            notEligible++;
            quietUntil.Remove(id);
            return true;
        }
        if (IsDangerNear(pawn))
        {
            dangerNear++;
            quietUntil.Remove(id);
            return true;
        }
        if (quietUntil.TryGetValue(id, out int until) && Find.TickManager.TicksGame < until)
        {
            skipped++;
            __result = null;
            return false;
        }
        if (depth++ == 0)
        {
            run++;
        }
        __state = true;
        return true;
    }

    public static void Postfix(Pawn pawn, Job __result, bool __state)
    {
        if (!__state)
        {
            return;
        }
        if (depth > 0)
        {
            depth--;
        }
        int now = Find.TickManager.TicksGame;
        if (__result == null)
        {
            if (quietUntil.Count > 4096)
            {
                foreach (var key in quietUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                {
                    quietUntil.Remove(key);
                }
            }
            quietUntil[pawn.thingIDNumber] = now + QuietTicks;
        }
        else
        {
            quietUntil.Remove(pawn.thingIDNumber);
        }
    }

    private static bool IsEligible(Pawn pawn)
    {
        if (!pawn.Spawned || !pawn.IsAnimal || pawn.Faction != Faction.OfPlayer || pawn.RaceProps.predator)
        {
            return false;
        }
        if (pawn.Downed || pawn.InMentalState || pawn.GetLord() != null)
        {
            return false;
        }
        return pawn.roping == null || !pawn.roping.IsRoped;
    }

    private static bool IsDangerNear(Pawn pawn)
    {
        var dangers = GetDangers(pawn.Map);
        var pos = pawn.Position;
        for (int i = 0; i < dangers.cells.Count; i++)
        {
            if ((dangers.cells[i] - pos).LengthHorizontalSquared <= dangers.radiusSquared[i])
            {
                return true;
            }
        }
        return false;
    }

    private static DangerList GetDangers(Map map)
    {
        int now = Find.TickManager.TicksGame;
        if (!dangerByMap.TryGetValue(map.uniqueID, out var list))
        {
            list = new DangerList();
            dangerByMap[map.uniqueID] = list;
        }
        if (now < list.until && now >= list.until - DangerRefreshTicks)
        {
            return list;
        }
        list.until = now + DangerRefreshTicks;
        list.cells.Clear();
        list.radiusSquared.Clear();
        float near = NearDistance * NearDistance;

        foreach (var target in map.attackTargetsCache.TargetsHostileToColony)
        {
            var thing = target.Thing;
            if (thing == null || !thing.Spawned || thing is Pawn { Downed: true })
            {
                continue;
            }
            list.cells.Add(thing.Position);
            list.radiusSquared.Add(near);
        }
        foreach (var other in map.mapPawns.AllPawnsSpawned)
        {
            if (other.Dead || other.Downed)
            {
                continue;
            }
            float radius = 0f;
            if (other.InMentalState)
            {
                radius = NearDistance;
            }
            else if (other.RaceProps.predator && (other.Faction != Faction.OfPlayer || other.CurJobDef == JobDefOf.PredatorHunt))
            {
                radius = NearDistance;
            }
            else if (IsAttackingJob(other.CurJob))
            {
                radius = NearDistance;
            }
            float carrier = CarrierRadius(other);
            if (carrier > 0f)
            {
                radius = System.Math.Max(radius, System.Math.Max(NearDistance, carrier + 8f));
            }
            if (radius > 0f)
            {
                list.cells.Add(other.Position);
                list.radiusSquared.Add(radius * radius);
            }
        }
        return list;
    }

    private static bool IsAttackingJob(Job job)
    {
        if (job == null || !(job.targetA.Thing is Pawn))
        {
            return false;
        }
        if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.PredatorHunt || job.def == JobDefOf.Hunt)
        {
            return true;
        }
        string name = job.def.defName;
        return name.IndexOf("Protect", System.StringComparison.Ordinal) >= 0 || name.IndexOf("Hunt", System.StringComparison.Ordinal) >= 0;
    }

    private static float CarrierRadius(Pawn pawn)
    {
        if (carrierRadius == null)
        {
            return 0f;
        }
        float radius = CarrierRadiusIn(pawn.kindDef?.modExtensions);
        return radius >= 0f ? radius : System.Math.Max(0f, CarrierRadiusIn(pawn.def.modExtensions));
    }

    private static float CarrierRadiusIn(List<DefModExtension> extensions)
    {
        if (extensions == null)
        {
            return -1f;
        }
        for (int i = 0; i < extensions.Count; i++)
        {
            if (carrierExtensionType.IsInstanceOfType(extensions[i]))
            {
                return carrierRadius(extensions[i]);
            }
        }
        return -1f;
    }
}
