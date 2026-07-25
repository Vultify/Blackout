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
        public const string Version = "2.0.0";

        // MoreBotsAPI's PREPATCHER guid (not its plugin guid) - this is what BepInEx orders us after
        public const string MoreBotsPrepatchGUID = "com.morebotsapiprepatch.tacticaltoaster";
    }

    // Registers our WildSpawnTypes into Assembly-CSharp before the client loads it: the Wedge (a boss)
    // and his guards. The guard is our own type named 'blackDivGuard' - it must NOT reuse the separate
    // BlackDiv mod's 'blackDivAssault' key, or the two collide and crash the client's settings init.
    public static class WildSpawnTypePatch
    {
        public const int WedgeSpawnType = 868588;   // Vultify's block, cleared with TacticalToaster
        public const int GuardSpawnType = 868589;

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
            // DifficultyModifier is SAIN's combat-skill dial. section is the SAIN GUI tab: the Wedge sits
            // under Bosses, his guards under their own Black Division tab.
            Register(assembly, WedgeSpawnType, "bossWedge", "Wedge", "Bosses", "The Wedge",
                "Black Division's commander, holding Labs through the blackout.",
                isBoss: true, isFollower: false, difficultyModifier: 0.9f);
            Register(assembly, GuardSpawnType, "blackDivGuard", "BlackDiv", "Black Division", "Black Division Guard",
                "A Black Division guard holding Labs at the Wedge's side.",
                isBoss: true, isFollower: true, difficultyModifier: 0.75f);

            // one group so the Wedge leads the guards as escorts
            CustomWildSpawnTypeManager.AddSuitableGroup(new List<int> { WedgeSpawnType, GuardSpawnType });
        }

        private static void Register(AssemblyDefinition assembly, int value, string name, string scavRole,
            string section, string sainName, string sainDesc, bool isBoss, bool isFollower, float difficultyModifier)
        {
            var type = new CustomWildSpawnType(value, name, scavRole, PmcBaseBrain, isBoss, isFollower);
            type.SetCountAsBossForStatistics(isBoss && !isFollower); // only the Wedge counts as a boss kill
            type.SetShouldUseFenceNoBossAttack(shouldUseForScav: false);
            type.SetExcludedDifficulties(new List<int> { 0, 2, 3 }); // normal only, as BlackDiv does
            type.SetSAINSettings(new SAINSettings(type.WildSpawnTypeValue)
            {
                Name = sainName,
                Description = sainDesc,
                Section = section,
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
