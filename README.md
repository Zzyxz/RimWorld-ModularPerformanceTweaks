# Modular Performance Tweaks

Source code of the RimWorld mod Modular Performance Tweaks. It makes slow spots of the game cheaper, mostly in big colonies, and every tweak can be switched on and off in the mod settings.

## Requirements

- RimWorld 1.6 or 1.7
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

## Building

Needs the .NET 10 SDK.

```
powershell -File Build.ps1
```

The DLLs go to `..\..\..\ColonyPerformanceTweaks\<version>\Assemblies`. The 1.7 build references Harmony through `HarmonyModDir` in the project file.

## License

MIT, see [LICENSE](LICENSE).
