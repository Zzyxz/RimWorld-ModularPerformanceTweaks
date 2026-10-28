using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace ColonyPerformanceTweaks;

public abstract class Tweak
{
    public abstract string Id { get; }

    public abstract string Category { get; }

    public bool Active { get; private set; }
    public string Problem { get; private set; }

    public bool On => ColonyPerformanceTweaksMod.Settings.IsOn(Id);

    public virtual string Unavailable => null;

    protected abstract void Apply(Harmony harmony);

    public void TryApply(Harmony harmony)
    {
        Problem = Unavailable;
        if (Problem != null)
        {
            return;
        }
        try
        {
            Apply(harmony);
            Active = true;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            Log.Warning("[Modular Performance Tweaks] " + Id + " is not active: " + e);
        }
    }

    public virtual string Stats() => null;

    public virtual void ResetStats()
    {
    }

    public virtual void ClearGame()
    {
    }

    public virtual void DrawOptions(Listing_Standard list)
    {
    }

    public virtual void Toggled(bool on)
    {
        ClearGame();
    }
}

public class ColonyPerformanceTweaksMod : Mod
{
    public static TweakSettings Settings;

    public static readonly List<Tweak> Tweaks = new()
    {
        new PenFoodCacheTweak(),
        new GatherableBatchingTweak(),
        new MeleeAnimationIdleTweak(),
        new CalmFleeTweak(),
        new ApparelPickTweak(),
        new ItemAccessibilityTweak(),
    };

    private Vector2 scroll;
    private float viewHeight = 1000f;

    public ColonyPerformanceTweaksMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<TweakSettings>();
        var harmony = new Harmony("Zzyxzz.ColonyPerformanceTweaks");
        foreach (var tweak in Tweaks)
        {
            tweak.TryApply(harmony);
        }
        try
        {
            harmony.Patch(AccessTools.Method(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)),
                prefix: new HarmonyMethod(typeof(ColonyPerformanceTweaksMod), nameof(ClearAllMapsAndWorldPrefix)));
        }
        catch (Exception e)
        {
            Log.Warning("[Modular Performance Tweaks] Leaving a game is not watched: " + e);
        }
    }

    public static void ClearAllMapsAndWorldPrefix()
    {
        foreach (var tweak in Tweaks)
        {
            try
            {
                tweak.ClearGame();
            }
            catch
            {
            }
        }
    }

    public override string SettingsCategory() => "Modular Performance Tweaks";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var view = new Rect(0f, 0f, inRect.width - 20f, viewHeight);
        Widgets.BeginScrollView(inRect, ref scroll, view);
        var list = new Listing_Standard();
        list.Begin(view);
        list.Label("CPT_Intro".Translate());
        list.GapLine();

        foreach (var group in Tweaks.GroupBy(t => t.Category))
        {
            Text.Font = GameFont.Medium;
            list.Label(group.Key.Translate());
            Text.Font = GameFont.Small;
            foreach (var tweak in group)
            {
                bool on = tweak.On;
                bool before = on;
                list.CheckboxLabeled(("CPT_" + tweak.Id).Translate(), ref on, ("CPT_" + tweak.Id + "Tip").Translate());
                if (on != before)
                {
                    Settings.Set(tweak.Id, on);
                    tweak.Toggled(on);
                }
                GUI.color = new Color(0.75f, 0.75f, 0.75f);
                if (tweak.Problem != null)
                {
                    string problem = tweak.Problem.StartsWith("CPT_", StringComparison.Ordinal) ? tweak.Problem.Translate().ToString() : tweak.Problem;
                    list.Label("CPT_NotActive".Translate(problem));
                }
                else if (!tweak.On)
                {
                    list.Label("CPT_SwitchedOff".Translate());
                }
                else
                {
                    list.Label(tweak.Stats() ?? "CPT_Active".Translate().ToString());
                }
                GUI.color = Color.white;
                if (tweak.Active && tweak.On)
                {
                    tweak.DrawOptions(list);
                }
                list.Gap(8f);
            }
            list.GapLine();
        }

        if (list.ButtonText("CPT_ResetStats".Translate()))
        {
            foreach (var tweak in Tweaks)
            {
                tweak.ResetStats();
            }
        }
        viewHeight = list.CurHeight + 20f;
        list.End();
        Widgets.EndScrollView();
    }
}

public class TweakSettings : ModSettings
{
    private Dictionary<string, bool> enabled = new();
    public int penFoodRememberTicks = 500;

    public bool IsOn(string id) => !enabled.TryGetValue(id, out bool on) || on;

    public void Set(string id, bool on)
    {
        enabled[id] = on;
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref enabled, "enabled", LookMode.Value, LookMode.Value);
        enabled ??= new Dictionary<string, bool>();
        Scribe_Values.Look(ref penFoodRememberTicks, "penFoodRememberTicks", 500);
    }
}
