using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ColonyPerformanceTweaks;

public sealed class MeleeAnimationIdleTweak : Tweak
{
    public override string Id => "MeleeAnimationIdle";
    public override string Category => "CPT_CategoryAnimals";

    private static MeleeAnimationIdleTweak instance;
    private static long skipped;
    private static AccessTools.FieldRef<object, float> rotOffset, rotVelocity;
    private static AccessTools.FieldRef<object, Vector2> posOffset, posVelocity;
    private static Func<Pawn, bool> isInAnimation;

    public override string Unavailable => AccessTools.TypeByName("AM.Idle.IdleControllerComp") == null ? "CPT_NeedsMeleeAnimation" : null;

    protected override void Apply(Harmony harmony)
    {
        var comp = AccessTools.TypeByName("AM.Idle.IdleControllerComp");
        rotOffset = AccessTools.FieldRefAccess<float>(comp, "dodgeRotationOffset");
        rotVelocity = AccessTools.FieldRefAccess<float>(comp, "dodgeRotationVelocity");
        posOffset = AccessTools.FieldRefAccess<Vector2>(comp, "dodgePositionOffset");
        posVelocity = AccessTools.FieldRefAccess<Vector2>(comp, "dodgePositionVelocity");
        var extensions = AccessTools.TypeByName("AM.Extensions");
        var inAnimation = extensions != null ? AccessTools.Method(extensions, "IsInAnimation", new[] { typeof(Pawn) }) : null;
        if (inAnimation == null)
        {
            throw new MissingMethodException("Melee Animation changed: IsInAnimation(Pawn) not found");
        }
        isInAnimation = AccessTools.MethodDelegate<Func<Pawn, bool>>(inAnimation);
        harmony.Patch(AccessTools.DeclaredMethod(comp, "CompTick"), prefix: new HarmonyMethod(typeof(MeleeAnimationIdleTweak), nameof(Prefix)));
        instance = this;
    }

    public override string Stats() => "CPT_MeleeAnimationIdleStats".Translate(skipped);

    public override void ResetStats()
    {
        skipped = 0;
    }

    public static bool Prefix(ThingComp __instance)
    {
        if (!instance.On || __instance.parent is not Pawn pawn || !pawn.RaceProps.Animal)
        {
            return true;
        }
        if (Mathf.Abs(rotOffset(__instance)) > 0.001f || Mathf.Abs(rotVelocity(__instance)) > 0.001f
            || posOffset(__instance).sqrMagnitude > 1e-6f || posVelocity(__instance).sqrMagnitude > 1e-6f
            || isInAnimation(pawn))
        {
            return true;
        }
        skipped++;
        return false;
    }
}
