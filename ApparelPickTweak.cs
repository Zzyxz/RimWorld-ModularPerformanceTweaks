using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class ApparelPickTweak : Tweak
{
    public override string Id => "ApparelPick";
    public override string Category => "CPT_CategoryNewPawns";

#if !NET
    public override string Unavailable => "CPT_Only17";

    protected override void Apply(Harmony harmony)
    {
    }
#else
    private const string OwnId = "Zzyxzz.ColonyPerformanceTweaks";

    private static ApparelPickTweak instance;
    private static MethodBase target, specificTarget, overlapTarget;
    private static FieldInfo workingSetField, allPairsField;
    private static Action<object, Pawn> resetSet;
    private static Action<object, ThingStuffPair> addToSet;
    private static Func<object, HashSet<ApparelUtility.LayerGroupPair>> setLayers;
    private static Func<Pawn, ThingStuffPair, bool> canUseStuff;

    private static bool conflictChecked, failed;
    private static string conflict;
    private static long pawns, picks;

    private static readonly Dictionary<(ThingDef thing, ThingDef stuff), float> weights = new();
    private static readonly Dictionary<(ThingDef thing, BodyDef body), ApparelUtility.LayerGroupPair[]> layersByBody = new();
    private static readonly Dictionary<SpecificApparelRequirement, List<ThingStuffPair>> pairsByRequirement = new();
    private static Dictionary<ThingDef, List<ThingStuffPair>> pairsByThing;

    private static int generation, cachedGeneration = -1, cachedCount = -1;
    private static List<ThingStuffPair> cachedCandidates;
    private static Pawn cachedPawn;
    private static BodyDef cachedBody;
    private static float[] candidateWeights = new float[256];
    private static ApparelUtility.LayerGroupPair[][] candidateLayers = new ApparelUtility.LayerGroupPair[256][];
    private static readonly List<int> usable = new();

    public override string Unavailable => null;

    protected override void Apply(Harmony harmony)
    {
        var generator = typeof(PawnApparelGenerator);
        var setType = AccessTools.Inner(generator, "PossibleApparelSet") ?? throw new MissingMemberException("PossibleApparelSet");
        target = AccessTools.Method(generator, "GenerateWorkingPossibleApparelSetFor") ?? throw new MissingMethodException("GenerateWorkingPossibleApparelSetFor");
        specificTarget = AccessTools.Method(generator, "GenerateSpecificRequiredApparel") ?? throw new MissingMethodException("GenerateSpecificRequiredApparel");
        overlapTarget = AccessTools.Method(setType, "PairOverlapsAnything") ?? throw new MissingMethodException("PairOverlapsAnything");
        workingSetField = AccessTools.Field(generator, "workingSet") ?? throw new MissingFieldException("workingSet");
        allPairsField = AccessTools.Field(generator, "allApparelPairs") ?? throw new MissingFieldException("allApparelPairs");
        var layersField = AccessTools.Field(setType, "lgps") ?? throw new MissingFieldException("PossibleApparelSet.lgps");
        if (layersField.FieldType != typeof(HashSet<ApparelUtility.LayerGroupPair>))
        {
            throw new MissingFieldException("PossibleApparelSet.lgps has another type");
        }

        var set = Expression.Parameter(typeof(object), "set");
        var pawn = Expression.Parameter(typeof(Pawn), "pawn");
        var pair = Expression.Parameter(typeof(ThingStuffPair), "pair");
        var typedSet = Expression.Convert(set, setType);
        resetSet = Expression.Lambda<Action<object, Pawn>>(
            Expression.Call(typedSet, AccessTools.Method(setType, "Reset", new[] { typeof(Pawn) }), pawn), set, pawn).Compile();
        addToSet = Expression.Lambda<Action<object, ThingStuffPair>>(
            Expression.Call(typedSet, AccessTools.Method(setType, "Add", new[] { typeof(ThingStuffPair) }), pair), set, pair).Compile();
        setLayers = Expression.Lambda<Func<object, HashSet<ApparelUtility.LayerGroupPair>>>(Expression.Field(typedSet, layersField), set).Compile();
        canUseStuff = AccessTools.MethodDelegate<Func<Pawn, ThingStuffPair, bool>>(AccessTools.Method(generator, "CanUseStuff"));

        CheckConflict();
        if (conflict != null)
        {
            throw new InvalidOperationException(conflict);
        }
        conflictChecked = false;
        harmony.Patch(AccessTools.Method(generator, nameof(PawnApparelGenerator.GenerateStartingApparelFor)),
            prefix: new HarmonyMethod(typeof(ApparelPickTweak), nameof(StartPrefix)));
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(ApparelPickTweak), nameof(Prefix)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(generator, nameof(PawnApparelGenerator.Reset)),
            postfix: new HarmonyMethod(typeof(ApparelPickTweak), nameof(ResetPostfix)));
        instance = this;
    }

    private static void CheckConflict()
    {
        conflictChecked = true;
        foreach (var method in new[] { target, specificTarget, overlapTarget })
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                continue;
            }
            bool bypassed = method != target;
            var other = info.Transpilers
                .Concat(info.Prefixes.Where(p => ChangesResult(p, isPrefix: true)))
                .Concat(bypassed ? info.Postfixes.Concat(info.Finalizers).Where(p => ChangesResult(p, isPrefix: false)) : Enumerable.Empty<Patch>())
                .FirstOrDefault(p => p.owner != OwnId);
            if (other != null)
            {
                conflict = "changed by " + other.owner;
                return;
            }
        }
    }

    private static bool ChangesResult(Patch patch, bool isPrefix)
    {
        var method = patch.PatchMethod;
        if (method == null)
        {
            return false;
        }
        if (isPrefix && method.ReturnType == typeof(bool))
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
        ? "CPT_ApparelPickConflict".Translate(conflict).ToString()
        : failed ? "CPT_ApparelPickFailed".Translate().ToString() : "CPT_ApparelPickStats".Translate(pawns, picks).ToString();

    public override void ResetStats()
    {
        pawns = picks = 0;
    }

    public override void ClearGame()
    {
        failed = false;
        cachedCandidates = null;
        cachedPawn = null;
        cachedBody = null;
        cachedGeneration = -1;
        weights.Clear();
    }

    public static void StartPrefix()
    {
        generation++;
    }

    public static void ResetPostfix()
    {
        weights.Clear();
        layersByBody.Clear();
        pairsByRequirement.Clear();
        pairsByThing = null;
        cachedCandidates = null;
    }

    public static bool Prefix(Pawn pawn, float money, List<ThingStuffPair> apparelCandidates)
    {
        if (instance == null || !instance.On || failed || pawn == null || apparelCandidates == null)
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
        try
        {
            Build(pawn, money, apparelCandidates);
            return false;
        }
        catch (Exception e)
        {
            failed = true;
            Log.Warning("[Modular Performance Tweaks] Faster apparel picks switched off after an error, the game's own code is used: " + e);
            return true;
        }
    }

    private static void Build(Pawn pawn, float money, List<ThingStuffPair> candidates)
    {
        object set = workingSetField.GetValue(null);
        resetSet(set, pawn);
        var body = pawn.RaceProps.body;
        var layers = setLayers(set);
        float moneyLeft = SpecificRequired(pawn, set, layers, body, money);
        var required = pawn.kindDef.apparelRequired;
        if (required != null)
        {
            for (int i = 0; i < required.Count; i++)
            {
                var def = required[i];
                if (def.apparel.CorrectAgeForWearing(pawn)
                    && PairsOf(def).Where(pa => canUseStuff(pawn, pa) && !Overlaps(layers, LayersOf(pa.thing, body))).TryRandomElementByWeight(pa => pa.Commonality, out var result))
                {
                    addToSet(set, result);
                    moneyLeft -= result.Price;
                }
            }
        }

        Prepare(pawn, body, candidates);
        usable.Clear();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!Overlaps(layers, candidateLayers[i]))
            {
                usable.Add(i);
            }
        }
        while ((pawn.Ideo == null || !pawn.Ideo.IdeoPrefersNudityForGender(pawn.gender) || (pawn.Faction != null && pawn.Faction.IsPlayer))
               && (pawn.IsColonist || pawn.story?.traits == null || !pawn.story.traits.HasTrait(TraitDefOf.Nudist))
               && (!(Rand.Value < 0.1f) || !(money < 9999999f))
               && TryPick(out int picked))
        {
            var chosen = candidates[picked];
            addToSet(set, chosen);
            moneyLeft -= chosen.Price;
            picks++;
            var added = candidateLayers[picked];
            for (int j = usable.Count - 1; j >= 0; j--)
            {
                int k = usable[j];
                if (candidates[k].Price > moneyLeft || Shares(candidateLayers[k], added))
                {
                    usable.RemoveAt(j);
                }
            }
        }
    }

    private static float SpecificRequired(Pawn pawn, object set, HashSet<ApparelUtility.LayerGroupPair> layers, BodyDef body, float moneyLeft)
    {
        var requirements = pawn.kindDef.specificApparelRequirements;
        if (requirements == null)
        {
            return moneyLeft;
        }
        for (int i = 0; i < requirements.Count; i++)
        {
            var req = requirements[i];
            if ((!req.RequiredTag.NullOrEmpty() || !req.AlternateTagChoices.NullOrEmpty())
                && PairsWithTags(req).Where(pa => PawnApparelGenerator.ApparelRequirementTagsMatch(req, pa.thing) && PawnApparelGenerator.ApparelRequirementHandlesThing(req, pa.thing)
                    && canUseStuff(pawn, pa) && pa.thing.apparel.PawnCanWear(pawn) && !Overlaps(layers, LayersOf(pa.thing, body))).TryRandomElementByWeight(pa => pa.Commonality, out var result))
            {
                addToSet(set, result);
                moneyLeft -= result.Price;
            }
        }
        return moneyLeft;
    }

    private static void Prepare(Pawn pawn, BodyDef body, List<ThingStuffPair> candidates)
    {
        if (cachedGeneration == generation && ReferenceEquals(cachedCandidates, candidates) && cachedPawn == pawn && cachedBody == body
            && cachedCount == candidates.Count && candidateWeights.Length >= candidates.Count)
        {
            return;
        }
        cachedGeneration = generation;
        cachedCandidates = candidates;
        cachedPawn = pawn;
        cachedBody = body;
        cachedCount = candidates.Count;
        pawns++;
        if (candidateWeights.Length < candidates.Count)
        {
            int size = Math.Max(candidates.Count, candidateWeights.Length * 2);
            candidateWeights = new float[size];
            candidateLayers = new ApparelUtility.LayerGroupPair[size][];
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            var pa = candidates[i];
            candidateWeights[i] = canUseStuff(pawn, pa) ? WeightOf(pa) : 0f;
            candidateLayers[i] = LayersOf(pa.thing, body);
        }
    }

    private static float WeightOf(ThingStuffPair pair)
    {
        if (!weights.TryGetValue((pair.thing, pair.stuff), out float weight))
        {
            weight = pair.Commonality;
            if (weight < 0f)
            {
                weight = 0f;
            }
            weights[(pair.thing, pair.stuff)] = weight;
        }
        return weight;
    }

    private static ApparelUtility.LayerGroupPair[] LayersOf(ThingDef thing, BodyDef body)
    {
        if (!layersByBody.TryGetValue((thing, body), out var result))
        {
            var list = new List<ApparelUtility.LayerGroupPair>();
            var props = thing.apparel;
            for (int i = 0; i < props.layers.Count; i++)
            {
                var groups = props.GetInterferingBodyPartGroups(body);
                for (int j = 0; j < groups.Length; j++)
                {
                    list.Add(new ApparelUtility.LayerGroupPair(props.layers[i], groups[j]));
                }
            }
            result = list.ToArray();
            layersByBody[(thing, body)] = result;
        }
        return result;
    }

    private static bool Overlaps(HashSet<ApparelUtility.LayerGroupPair> taken, ApparelUtility.LayerGroupPair[] pairs)
    {
        if (taken.Count == 0)
        {
            return false;
        }
        for (int i = 0; i < pairs.Length; i++)
        {
            if (taken.Contains(pairs[i]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Shares(ApparelUtility.LayerGroupPair[] a, ApparelUtility.LayerGroupPair[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                if (a[i].Equals(b[j]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static List<ThingStuffPair> AllPairs() => (List<ThingStuffPair>)allPairsField.GetValue(null);

    private static List<ThingStuffPair> PairsOf(ThingDef thing)
    {
        if (pairsByThing == null)
        {
            pairsByThing = new Dictionary<ThingDef, List<ThingStuffPair>>();
            foreach (var pa in AllPairs())
            {
                if (!pairsByThing.TryGetValue(pa.thing, out var list))
                {
                    list = new List<ThingStuffPair>();
                    pairsByThing[pa.thing] = list;
                }
                list.Add(pa);
            }
        }
        return pairsByThing.TryGetValue(thing, out var found) ? found : new List<ThingStuffPair>();
    }

    private static List<ThingStuffPair> PairsWithTags(SpecificApparelRequirement req)
    {
        if (!pairsByRequirement.TryGetValue(req, out var list))
        {
            var tags = new HashSet<string>();
            if (!req.RequiredTag.NullOrEmpty())
            {
                tags.Add(req.RequiredTag);
            }
            if (req.AlternateTagChoices != null)
            {
                foreach (var choice in req.AlternateTagChoices)
                {
                    tags.Add(choice.tag);
                }
            }
            list = AllPairs().Where(pa => pa.thing.apparel.tags != null && pa.thing.apparel.tags.Any(tags.Contains)).ToList();
            pairsByRequirement[req] = list;
        }
        return list;
    }

    private static bool TryPick(out int picked)
    {
        picked = -1;
        float total = 0f;
        for (int k = 0; k < usable.Count; k++)
        {
            total += candidateWeights[usable[k]];
        }
        if (total <= 0f)
        {
            return false;
        }
        float roll = Rand.Value * total;
        for (int k = 0; k < usable.Count; k++)
        {
            float w = candidateWeights[usable[k]];
            if (w <= 0f)
            {
                continue;
            }
            picked = usable[k];
            roll -= w;
            if (roll <= 0f)
            {
                return true;
            }
        }
        return picked >= 0;
    }
#endif
}
