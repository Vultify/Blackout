using System.Collections.Generic;
using BepInEx;
using Mono.Cecil;
using MoreBotsAPI;

namespace BlackoutPrepatch
{
    public static class ClientInfo
    {
        public const string GUID = "com.vultify.blackout.prepatch";
        public const string Name = "Blackout Prepatch";
        public const string Version = "1.0.0";

        // MoreBotsAPI's PREPATCHER guid (not its plugin guid) - this is what BepInEx orders us after
        public const string MoreBotsPrepatchGUID = "com.morebotsapiprepatch.tacticaltoaster";
    }

    // Registers our own Black Division WildSpawnTypes into Assembly-CSharp before the client loads it.
    // Both are ours, so they share one suitable group with no conflict - unlike depending on the
    // separate BlackDiv mod, whose types already claim their own group.
    public static class WildSpawnTypePatch
    {
        public const int WedgeSpawnType = 868588;   // Vultify's block, cleared with TacticalToaster
        public const int SoldierSpawnType = 868589;

        // SAIN "PMC brain" setup, matching how Black Division bots behave
        private const int PmcBaseBrain = 9;
        private static readonly List<string> Brains = new List<string> { "PMC", "ExUsec" };
        private static readonly List<string> StripLayers = new List<string>
        {
            "Request", "KnightFight", "PmcBear", "PmcUsec", "ExURequest", "StationaryWS",
        };

        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        public static void Patch(ref AssemblyDefinition assembly)
        {
            // scavRole is the raid-end / kill-screen role label (needs a matching locale, added server-side).
            // Wedge gets his own role so he reads "The Wedge"; soldiers read "Black Division".
            // DifficultyModifier is SAIN's combat-skill dial. The Wedge fights boss-grade (.9); the guards
            // and wave soldiers match BlackDiv, which leaves it at SAIN's default (.5).
            Register(assembly, WedgeSpawnType, "bossWedge", "Wedge", "The Wedge",
                "Black Division's commander, holding Labs through the blackout.",
                isBoss: true, isFollower: false, difficultyModifier: 0.9f);
            Register(assembly, SoldierSpawnType, "blackDivAssault", "BlackDiv", "Black Division",
                "A Black Division operative holding Labs.",
                isBoss: true, isFollower: true, difficultyModifier: 0.5f);

            // one group for all of ours - lets Wedge lead the soldiers as escorts
            CustomWildSpawnTypeManager.AddSuitableGroup(new List<int> { WedgeSpawnType, SoldierSpawnType });
        }

        private static void Register(AssemblyDefinition assembly, int value, string name, string scavRole,
            string sainName, string sainDesc, bool isBoss, bool isFollower, float difficultyModifier)
        {
            var type = new CustomWildSpawnType(value, name, scavRole, PmcBaseBrain, isBoss, isFollower);
            type.SetCountAsBossForStatistics(isBoss && !isFollower); // only the Wedge counts as a boss kill
            type.SetShouldUseFenceNoBossAttack(shouldUseForScav: false);
            type.SetExcludedDifficulties(new List<int> { 0, 2, 3 }); // normal only, as BlackDiv does
            type.SetSAINSettings(new SAINSettings(type.WildSpawnTypeValue)
            {
                Name = sainName,
                Description = sainDesc,
                Section = "Black Division",
                BaseBrain = "PMC",
                BrainsToApply = Brains,
                LayersToRemove = StripLayers,
                DifficultyModifier = difficultyModifier,
            });
            CustomWildSpawnTypeManager.RegisterWildSpawnType(type, assembly);
        }
    }

    [BepInDependency(ClientInfo.MoreBotsPrepatchGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(ClientInfo.GUID, ClientInfo.Name, ClientInfo.Version)]
    public class BlackoutPrepatchPlugin : BaseUnityPlugin
    {
    }
}
