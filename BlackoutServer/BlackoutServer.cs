using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace BlackoutServer
{
    public record BlackoutServerMetadata : AbstractModMetadata
    {
        public override string ModGuid { get; init; } = "com.vultify.blackout";
        public override string Name { get; init; } = "Blackout";
        public override string Author { get; init; } = "Vultify";
        public override string License { get; init; } = "MIT";
        public override string Url { get; init; } = "";
        // ships AssetBundles for the Wedge gear - must be true or the client never requests them
        public override bool? IsBundleMod { get; init; } = false;

        public override SemanticVersioning.Version Version { get; init; }
            = new SemanticVersioning.Version("1.1.0", false);

        public override SemanticVersioning.Range SptVersion { get; init; }
            = new SemanticVersioning.Range("~4.0.13", false);

        public override List<string> Contributors { get; init; } = new();
        public override List<string> Incompatibilities { get; init; } = new();
        public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new()
        {
            // 2.0.22 is what Content Backport 1.1.0 itself requires - an older one loads but leaves CB broken
            { "com.wtt.commonlib", new SemanticVersioning.Range(">=2.0.22") },
            // 1.1.0 is a hard floor now: every piece of Wedge's gear, his face, clothing and voice are
            // Content Backport items, and they don't exist before it
            { "com.wtt.contentbackport", new SemanticVersioning.Range(">=1.1.0") },
            // the Wedge is our own MoreBotsAPI boss type
            { "com.morebotsapi.tacticaltoaster", new SemanticVersioning.Range(">=2.0.0") },
        };
    }

    // Every item the mod adds, created through WTT-CommonLib from db/CustomItems/ - the Wedge's gear
    // (wedge_gear.json) and the event's Admin's key (blackout_key.json). Runs post-DB so the clone
    // donors already exist. Deliberately one path for all items rather than mixing in SPT's own
    // CustomItemService, so item definitions live in JSON and behave identically.
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 4)]
    public class BlackoutCustomItems : IOnLoad
    {
        // the live event's Admin's key, real 1.0.6.5 item id - opens the system admin office
        private const string AdminKey = "6a33c17933cff6b88c08902e";

        // MP7 host weapons + the two mod items whose slot chain we wire by hand
        private const string Mp7a1 = "5ba26383d4351e00334c93d9";
        private const string Mp7a2 = "5bd70322209c4d00d7167b8f";
        private const string ArsAdapter = "69985e9146e48aa39d06a685";
        private const string FxKposStock = "69985e7819f8713b630de3d6";
        private const string Sf3pMuzzle = "69985fa69f79621e1d0f58f5";

        // SureFire SOCOM556 suppressors that mount on an SF3P flash hider (same set the real AR15 SF3P takes)
        private static readonly string[] Socom556Suppressors =
        {
            "55d6190f4bdc2d87028b4567", // Mini Monster
            "55d614004bdc2d86028b4568", // Monster
            "5ea17bbc09aa976f2e7a51cd", // RC2
        };

        // Content Backport's black AN/PEQ-15 is a new clone id, so it's absent from every vanilla weapon's
        // tactical filter - the MP7 must be told to accept it (matches Wedge's real preset).
        private const string AnPeq15Black = "68bedc0365e7dcf94f0cb0fc";

        // all Content Backport's, as of its 1.1.0. Wedge wears the VANILLA black EXFIL now: CB adds a
        // mod_equipment_002 slot to it in code and puts its own cover in there, which is what our repacked
        // helmet used to exist for.
        private const string ExfilMulticamHelmet = "69c26722bf4ff19f50057643";
        private const string WedgeCoverHelmet = "69e24f4f9e6ca1b32508bfbc";
        private const string BlackExfilHelmet = "5e00c1ad86f774747333222c";
        private const string CoyoteExfilHelmet = "5e01ef6886f77445f643baa4";
        // the helmet-mounted variant, not CB's standalone black ComTac VI
        private const string ComTacVIBlack = "69c264c00f660b3f0d058fcf";

        // Spiritus LV-119: Content Backport 1.1.0 ships this rig itself, with the same 15 pouches, its own
        // klin_rig layout and a preset that installs both soft armor inserts and both plates - so we use
        // theirs rather than duplicating it. Ours used to live here and collided with their bundle.
        private const string Lv119Rig = "69e2441a18cb3157560855ec";
        private const string Lv119Layout = "klin_rig";

        private readonly WTTServerCommonLib.WTTServerCommonLib _commonLib;
        private readonly DatabaseService _databaseService;
        private readonly ISptLogger<BlackoutCustomItems> _logger;

        public BlackoutCustomItems(
            WTTServerCommonLib.WTTServerCommonLib commonLib,
            DatabaseService databaseService,
            ISptLogger<BlackoutCustomItems> logger)
        {
            _commonLib = commonLib;
            _databaseService = databaseService;
            _logger = logger;
        }

        public async Task OnLoad()
        {
            try
            {
                await _commonLib.CustomItemServiceExtended.CreateCustomItems(Assembly.GetExecutingAssembly());

                var items = _databaseService.GetItems();

                // Content Backport's addtoModSlots should already put the adapter and the SF3P on the MP7,
                // but do it anyway - it's idempotent, and depending on another mod's load order for whether
                // Wedge has a stock and a suppressor is not worth the risk
                AddToSlot(items, Mp7a1, "mod_stock", ArsAdapter);
                AddToSlot(items, Mp7a2, "mod_stock", ArsAdapter);
                AddToSlot(items, Mp7a1, "mod_muzzle", Sf3pMuzzle);
                AddToSlot(items, Mp7a2, "mod_muzzle", Sf3pMuzzle);
                // the adapter accepts only the FX-KPOS stock, which CB does not wire
                SetSlotFilter(items, ArsAdapter, "mod_stock", FxKposStock);
                // the SF3P's own mod_muzzle accepts the SOCOM556 suppressors, not the inherited MP7 Rotex.
                // NOT _required - the gunsmith paints a required-but-empty slot red (no vanilla muzzle
                // device requires its can); the Wedge's always-on suppressor is enforced per-role in
                // BlackoutBots instead, where only bot generation can see it
                var sf3pMuzzleSlot = FindSlot(items, Sf3pMuzzle, "mod_muzzle")?.Properties?.Filters?.FirstOrDefault();
                if (sf3pMuzzleSlot != null)
                {
                    sf3pMuzzleSlot.Filter = new HashSet<MongoId>(Socom556Suppressors.Select(id => new MongoId(id)));
                }
                // the black AN/PEQ-15 goes in every MP7 tactical slot (all three, so it mounts anywhere the
                // vanilla one does) - without this the gunsmith rejects it since it's a new backport id
                foreach (var slot in new[] { "mod_tactical_000", "mod_tactical_001", "mod_tactical_002" })
                {
                    AddToSlot(items, Mp7a1, slot, AnPeq15Black);
                    AddToSlot(items, Mp7a2, slot, AnPeq15Black);
                }

                // no armour presets and no cover slot here any more - Content Backport ships a preset for its
                // MultiCam helmet, and adds the mod_equipment_002 cover slot to the vanilla black EXFIL itself

                // the black ComTac VI is a helmet-mounted headset and CB does NOT slot it anywhere
                // (addtoModSlots false), so the EXFIL helmets still have to be told to accept it
                foreach (var helmet in new[] { BlackExfilHelmet, CoyoteExfilHelmet, ExfilMulticamHelmet })
                {
                    AddToSlot(items, helmet, "mod_equipment_000", ComTacVIBlack);
                }

                // the Admin's key is the only item Blackout still creates; everything else is CB's
                var made = items.ContainsKey(new MongoId(AdminKey)) ? 1 : 0;

                // single use; a wrong clone would silently inherit the donor's usage limit instead
                items.TryGetValue(new MongoId(AdminKey), out var adminKey);
                var keyUses = adminKey?.Properties?.MaximumNumberOfUsage;
                var keySellable = adminKey?.Properties?.CanSellOnRagfair;
                // prices live outside the item template, so check the tables they actually land in
                var keyHandbook = _databaseService.GetHandbook().Items?
                    .FirstOrDefault(i => i.Id == new MongoId(AdminKey))?.Price;
                _databaseService.GetPrices().TryGetValue(new MongoId(AdminKey), out var keyFlea);

                // Content Backport's, not ours - assert it landed, because a silently missing cover slot is
                // the difference between Wedge wearing his helmet cover and not
                var coverSlot = SlotContains(items, BlackExfilHelmet, "mod_equipment_002", WedgeCoverHelmet);
                var comtacMounted = SlotContains(items, BlackExfilHelmet, "mod_equipment_000", ComTacVIBlack);
                // the two filter patches the gunsmith AND bot generation both depend on - assert the
                // written state, a silent FindSlot miss here is exactly how a suppressor vanishes.
                // Required must stay FALSE or the gunsmith flags a bare SF3P as an incomplete part
                var socomOnSf3p = SlotContains(items, Sf3pMuzzle, "mod_muzzle", Socom556Suppressors[0])
                    && FindSlot(items, Sf3pMuzzle, "mod_muzzle")?.Required != true;
                var peqOnMp7 = SlotContains(items, Mp7a1, "mod_tactical_000", AnPeq15Black);

                // the rig is Content Backport's now - assert it actually turned up rather than assuming the
                // dependency shipped what we expect, since Wedge's armour is unwearable without it
                items.TryGetValue(new MongoId(Lv119Rig), out var rig);
                var rigGrids = rig?.Properties?.Grids?.Count() ?? 0;
                var rigLayout = rig?.Properties?.RigLayoutName ?? "(none)";

                if (made == 1 && keyUses == 1 && keySellable == true && keyHandbook == 100000 && keyFlea == 157434
                    && SlotContains(items, Mp7a1, "mod_stock", ArsAdapter)
                    && SlotContains(items, Mp7a1, "mod_muzzle", Sf3pMuzzle) && coverSlot && comtacMounted
                    && socomOnSf3p && peqOnMp7
                    && rigGrids == 15 && rigLayout == Lv119Layout)
                {
                    // everything above is asserted, not just counted - the detail only prints on failure.
                    // Most of it is Content Backport's gear now, so this doubles as a check that the
                    // dependency still ships what Wedge's loadout expects
                    _logger.Success("[Blackout] Admin's key created; Wedge's gear resolved from Content Backport.");
                }
                else
                {
                    _logger.Error($"[Blackout] Gear incomplete - admin key {made}/1, key uses {keyUses} (want 1), " +
                        $"key sellable {keySellable}, handbook {keyHandbook} (want 100000), flea {keyFlea} (want 157434), " +
                        $"adapter on MP7 {SlotContains(items, Mp7a1, "mod_stock", ArsAdapter)}, " +
                        $"SF3P on MP7 {SlotContains(items, Mp7a1, "mod_muzzle", Sf3pMuzzle)}, " +
                        $"cover slot {coverSlot}, ComTac VI {comtacMounted}, " +
                        $"SOCOM on SF3P {socomOnSf3p}, black PEQ on MP7 {peqOnMp7}, " +
                        $"CB LV-119 {rigGrids} pouches, layout '{rigLayout}'; check the CB version.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Wedge gear load failed: {ex}");
            }
        }

        private static Slot? FindSlot(IDictionary<MongoId, TemplateItem> items, string hostId, string slotName)
        {
            return items.TryGetValue(new MongoId(hostId), out var host)
                ? host.Properties?.Slots?.FirstOrDefault(s => s.Name == slotName)
                : null;
        }

        private static bool SlotContains(IDictionary<MongoId, TemplateItem> items, string hostId, string slotName, string modId)
        {
            var f = FindSlot(items, hostId, slotName)?.Properties?.Filters?.FirstOrDefault();
            return f?.Filter?.Contains(new MongoId(modId)) ?? false;
        }

        private static void AddToSlot(IDictionary<MongoId, TemplateItem> items, string hostId, string slotName, string modId)
        {
            var filter = FindSlot(items, hostId, slotName)?.Properties?.Filters?.FirstOrDefault();
            if (filter?.Filter != null && !filter.Filter.Contains(new MongoId(modId)))
            {
                filter.Filter.Add(new MongoId(modId));
            }
        }

        private static void SetSlotFilter(IDictionary<MongoId, TemplateItem> items, string hostId, string slotName, string onlyModId)
        {
            var filter = FindSlot(items, hostId, slotName)?.Properties?.Filters?.FirstOrDefault();
            if (filter != null)
            {
                filter.Filter = new HashSet<MongoId> { new MongoId(onlyModId) };
            }
        }
    }

    // The Wedge and his guards share this faction, so they never fight each other. We register it
    // ourselves at LoadFactions so it exists before any hostility wiring.
    [Injectable(TypePriority = MoreBotsServer.MoreBotsLoadOrder.LoadFactions + 1)]
    public class BlackoutFaction : IOnLoad
    {
        public const string FactionName = "blackdivision";

        private readonly MoreBotsServer.Services.FactionService _factionService;
        private readonly ISptLogger<BlackoutFaction> _logger;

        public BlackoutFaction(MoreBotsServer.Services.FactionService factionService, ISptLogger<BlackoutFaction> logger)
        {
            _factionService = factionService;
            _logger = logger;
        }

        public Task OnLoad()
        {
            try
            {
                var faction = new Faction { Name = FactionName, RevengeAfterRaids = false };
                faction.BotTypes.Add((WildSpawnType)BlackoutBots.WedgeSpawnType);
                faction.BotTypes.Add((WildSpawnType)BlackoutBots.GuardSpawnType);
                _factionService.Factions[FactionName] = faction;
                _logger.Success($"[Blackout] Faction '{FactionName}' registered ({faction.BotTypes.Count} types).");
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Faction registration failed: {ex}");
            }
            return Task.CompletedTask;
        }
    }

    // Creates the Wedge boss and his Black Division Guard escorts, and wires their gear and hostility.
    // The Wedge uses our own type with our 9 backported gear items; the guards use our own type
    // ('blackDivGuard') off the recovered BlackDiv-style loadout. Both are also set friendly toward the
    // separate BlackDiv mod so everyone holds Labs together.
    [Injectable(TypePriority = MoreBotsServer.MoreBotsLoadOrder.LoadBots + 1)]
    public class BlackoutBots : IOnLoad
    {
        public const int WedgeSpawnType = 868588;
        public const int GuardSpawnType = 868589;
        public const string WedgeName = "bossWedge";
        public const string GuardName = "blackDivGuard";

        private const string ArmoryGuid = "com.wtt.armory";
        // the separate BlackDiv mod's faction - our bots are set friendly toward it when present
        private const string BlackDivFaction = "blackdiv";
        private static readonly string[] EnemyFactions = { "savage", "rogues", "usec", "bear", "infected" };

        private readonly MoreBotsServer.MoreBotsAPI _moreBots;
        private readonly MoreBotsServer.Services.MoreBotsCustomBotTypeService _customBotTypeService;
        private readonly MoreBotsServer.Services.FactionService _factionService;
        private readonly WTTServerCommonLib.WTTServerCommonLib _commonLib;
        private readonly DatabaseService _databaseService;
        private readonly ConfigServer _configServer;
        private readonly IReadOnlyList<SptMod> _modList;
        private readonly ISptLogger<BlackoutBots> _logger;

        public BlackoutBots(
            MoreBotsServer.MoreBotsAPI moreBots,
            MoreBotsServer.Services.MoreBotsCustomBotTypeService customBotTypeService,
            MoreBotsServer.Services.FactionService factionService,
            WTTServerCommonLib.WTTServerCommonLib commonLib,
            DatabaseService databaseService,
            ConfigServer configServer,
            IReadOnlyList<SptMod> modList,
            ISptLogger<BlackoutBots> logger)
        {
            _moreBots = moreBots;
            _customBotTypeService = customBotTypeService;
            _factionService = factionService;
            _commonLib = commonLib;
            _databaseService = databaseService;
            _configServer = configServer;
            _modList = modList;
            _logger = logger;
        }

        public async Task OnLoad()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // both our types from db/bots/types + db/bots/config: the Wedge (bosswedge) and his
                // guards (blackdivguard, off the recovered BlackDiv-style loadout)
                await _moreBots.LoadBots(assembly);

                // the ScavRole -> display-name locale (db/CustomLocales), so the kill screen reads
                // "Black Div" instead of the raw ScavRole key
                await _commonLib.CustomLocaleService.CreateCustomLocales(assembly, null);

                // his head/top/pants/voice come from Content Backport now - it ships the same ripped-live
                // bundles, and two mods registering the same asset paths is what broke the load

                _customBotTypeService.AddCustomWildSpawnTypeNames(new Dictionary<int, string>
                {
                    { WedgeSpawnType, WedgeName },
                    { GuardSpawnType, GuardName },
                });

                // keep both at 1: the Wedge is a lone boss, and a 4-5 guard escort already generates the
                // group in parallel - a higher batch just inflates that burst into the shared-pool race
                var botConfig = _configServer.GetConfig<BotConfig>();
                botConfig.PresetBatch[WedgeName] = 1;
                botConfig.PresetBatch[GuardName] = 1;

                // the Wedge's suppressor must never roll off: the generator rewrites mod_muzzle chance
                // to 95 once a muzzle device installs, and a failed roll on a role-required slot falls
                // back to the bot's own mod pool (the SOCOM). Per-role config, so the gunsmith never
                // sees a required slot on the SF3P item itself
                if (!botConfig.Equipment.TryGetValue(WedgeName.ToLowerInvariant(), out var wedgeEquip) || wedgeEquip == null)
                {
                    wedgeEquip = new EquipmentFilters();
                    botConfig.Equipment[WedgeName.ToLowerInvariant()] = wedgeEquip;
                }
                wedgeEquip.WeaponSlotIdsToMakeRequired ??= new HashSet<string>();
                wedgeEquip.WeaponSlotIdsToMakeRequired.Add("mod_muzzle");

                // the guard's loadout: base always (BlackDiv-style weapons that exist without Armory),
                // the Armory arsenal only when WTT-Armory is installed - BlackDiv's graceful-degrade pattern
                await _commonLib.CustomBotLoadoutService.CreateCustomBotLoadouts(assembly, null);
                var hasArmory = _modList.Any(m => m.ModMetadata.ModGuid == ArmoryGuid);
                if (hasArmory)
                {
                    await _commonLib.CustomBotLoadoutService.CreateCustomBotLoadouts(
                        assembly, System.IO.Path.Combine("db", "ModBotLoadouts", "Armory"));
                }

                // hostility, both directions explicitly (the API's own consumers always do). the Wedge
                // and his guards fight the player (usec/bear), scavs and rogues - everyone except Black Division.
                var mine = new[] { WedgeName, GuardName };
                foreach (var faction in EnemyFactions)
                {
                    _factionService.AddEnemyByFaction(mine, faction);
                    _factionService.AddEnemyByFaction(faction, BlackoutFaction.FactionName);
                }

                // friendly toward the separate BlackDiv mod so the Wedge and their troops hold Labs
                // together rather than fighting. no-ops harmlessly if that mod is not installed.
                try
                {
                    _factionService.AddFriendlyByFaction(mine, BlackDivFaction);
                    _factionService.AddFriendlyByFaction(BlackDivFaction, BlackoutFaction.FactionName);
                }
                catch (Exception ex)
                {
                    _logger.Info($"[Blackout] BlackDiv friendliness not wired (mod not present?): {ex.Message}");
                }

                // read the types back out of the database rather than trusting the calls
                var bots = _databaseService.GetBots().Types;
                var wedgeOk = bots.TryGetValue(WedgeName.ToLowerInvariant(), out var wedge);
                var guardOk = bots.ContainsKey(GuardName.ToLowerInvariant());

                // his head/top/pants/voice are Content Backport's - check they're actually in the
                // customization DB, else he spawns with a broken appearance and nothing logs it
                var cust = _databaseService.GetCustomization();
                string[] wedgeAppearance = { "69e24393d10363e6f90064d0", "69e2427109707df7660efa26",
                    "69e24294e0d3dc5cfd031434", "69c68f1a8f75eda7610edac4" };
                var appearanceOk = wedgeAppearance.Count(id => cust.ContainsKey(new MongoId(id)));
                var wedgeHp = wedgeOk
                    ? (int)(wedge!.BotHealth?.BodyParts?.FirstOrDefault() is { } bp
                        ? bp.Chest.Max + bp.Head.Max + bp.LeftArm.Max + bp.LeftLeg.Max
                          + bp.RightArm.Max + bp.RightLeg.Max + bp.Stomach.Max
                        : 0)
                    : 0;

                if (wedgeOk && guardOk && appearanceOk == 4)
                {
                    _logger.Success($"[Blackout] Wedge ('{WedgeName}', {wedgeHp} HP, own appearance) + guards " +
                        $"('{GuardName}'); Armory loadout: {(hasArmory ? "on" : "off (base weapons)")}; " +
                        $"hostile to {EnemyFactions.Length} factions, friendly to Black Division.");
                }
                else
                {
                    _logger.Error($"[Blackout] Bot load incomplete - Wedge={wedgeOk}, guard={guardOk}, " +
                        $"Wedge appearance {appearanceOk}/4 in customization DB; " +
                        "a silent skip means a bad type/customization file, check the WTT log above.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Bot load failed: {ex}");
            }
        }
    }

    // Places the Wedge and his guards on Labs. Spawn data bakes into the location at generation time,
    // so it is injected here and re-injected after every raid.
    [Injectable(InjectionType.Singleton)]
    public class BlackoutSpawnController
    {
        // BOTH gate zones (Gate1 z~-225, Gate2 z~-451) are sealed ramps behind the power-gated doors -
        // only Floor1/Floor2 are in the map's OpenZones, so the Wedge holds an open floor. He and his
        // guards roam from there; no waves.
        private const string WedgeZone = "BotZoneFloor2";
        private const string GuardEscorts = "4,4,5,5"; // Wedge + 4-5 guards

        private const double DefaultChance = 25;

        private readonly DatabaseService _databaseService;
        private readonly RandomUtil _randomUtil;
        private readonly ISptLogger<BlackoutSpawnController> _logger;
        private readonly double _chance;

        // the server owns the coin flip: the Wedge is a server-side spawn baked into the location
        // before the client is even in the raid, so the roll has to live here. the client reads the
        // result off /blackout/state so the darkness and the boss agree on the same flip
        public bool CurrentRaidBlackout { get; private set; }

        public BlackoutSpawnController(DatabaseService databaseService, RandomUtil randomUtil, ISptLogger<BlackoutSpawnController> logger)
        {
            _databaseService = databaseService;
            _randomUtil = randomUtil;
            _logger = logger;
            _chance = LoadChance();
        }

        // config.json sits next to our dll, copy-if-missing on install so a player's edit survives updates
        private double LoadChance()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(BlackoutSpawnController).Assembly.Location);
                var path = System.IO.Path.Combine(dir!, "config.json");
                if (System.IO.File.Exists(path))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(
                        System.IO.File.ReadAllText(path),
                        new System.Text.Json.JsonDocumentOptions
                        {
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        });
                    if (doc.RootElement.TryGetProperty("blackoutChance", out var v))
                    {
                        var chance = Math.Clamp(v.GetDouble(), 0, 100);
                        _logger.Success($"[Blackout] blackout chance {chance}% per Labs raid (from config.json).");
                        return chance;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Blackout] config.json unreadable, using {DefaultChance}%: {ex.Message}");
            }
            return DefaultChance;
        }

        public int Inject()
        {
            var labs = _databaseService.GetLocations().Laboratory;
            if (labs?.Base?.BossLocationSpawn == null)
            {
                _logger.Error("[Blackout] Labs location unavailable - Black Division not injected.");
                return 0;
            }

            var spawns = labs.Base.BossLocationSpawn.ToList();
            // identified by boss type - the base Labs map only spawns pmcBot as bosses, so removing the
            // Wedge is safe and makes re-injection idempotent
            spawns.RemoveAll(s => s.BossName == BlackoutBots.WedgeName);

            // roll for this raid. a failed roll leaves Labs completely vanilla - no Wedge here, and the
            // client reads the same result and skips the darkness, lockdown, keypads and the locked door
            CurrentRaidBlackout = _randomUtil.GetChance100(_chance);
            if (!CurrentRaidBlackout)
            {
                labs.Base.BossLocationSpawn = spawns;
                _logger.Info($"[Blackout] Roll failed ({_chance}% chance) - this Labs raid stays normal.");
                return 0;
            }

            // the Wedge leading 4-5 guards on one open floor at raid start, no waves
            spawns.Add(new BossLocationSpawn
            {
                BossName = BlackoutBots.WedgeName,
                BossChance = 100,
                BossDifficulty = "normal",
                BossEscortType = BlackoutBots.GuardName,
                BossEscortAmount = GuardEscorts,
                BossEscortDifficulty = "normal",
                BossZone = WedgeZone,
                Time = -1,
                IsBossPlayer = false,
                IsRandomTimeSpawn = false,
                Delay = 0,
                ForceSpawn = false,
                IgnoreMaxBots = true,
                SpawnMode = ["regular", "pve"],
            });

            labs.Base.BossLocationSpawn = spawns;
            return 1;
        }
    }

    [Injectable(TypePriority = MoreBotsServer.MoreBotsLoadOrder.LoadBots + 2)]
    public class BlackoutSpawns : IOnLoad
    {
        private readonly BlackoutSpawnController _controller;
        private readonly ISptLogger<BlackoutSpawns> _logger;

        public BlackoutSpawns(BlackoutSpawnController controller, ISptLogger<BlackoutSpawns> logger)
        {
            _controller = controller;
            _logger = logger;
        }

        public Task OnLoad()
        {
            try
            {
                _controller.Inject();
                _logger.Success("[Blackout] Labs spawns rolled: Wedge + 4-5 guards on a blackout raid, vanilla otherwise.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Spawn injection failed: {ex}");
            }
            return Task.CompletedTask;
        }
    }

    // Spawn data is rebuilt per raid, so re-inject (and re-roll) once the previous one ends.
    [Injectable]
    public class BlackoutRaidEndRouter : StaticRouter
    {
        private static BlackoutSpawnController _controller = null!;

        public BlackoutRaidEndRouter(JsonUtil jsonUtil, BlackoutSpawnController controller)
            : base(jsonUtil, GetRoutes())
        {
            _controller = controller;
        }

        private static List<RouteAction> GetRoutes()
        {
            return new List<RouteAction>
            {
                new RouteAction("/client/match/local/end",
                    async (url, info, sessionID, output) =>
                    {
                        _controller.Inject();
                        return await new ValueTask<object>(output ?? string.Empty);
                    }, null),
            };
        }
    }

    // Tells the client whether THIS raid rolled a blackout, so the darkness, the extract lockdown, the
    // keypads and the locked arsenal door all ride the same flip as the Wedge instead of rolling apart.
    [Injectable]
    public class BlackoutStateRouter : StaticRouter
    {
        private static BlackoutSpawnController _controller = null!;

        public BlackoutStateRouter(JsonUtil jsonUtil, BlackoutSpawnController controller)
            : base(jsonUtil, GetRoutes())
        {
            _controller = controller;
        }

        private static List<RouteAction> GetRoutes()
        {
            return new List<RouteAction>
            {
                new RouteAction("/blackout/state",
                    async (url, info, sessionID, output) =>
                        await new ValueTask<object>(
                            _controller.CurrentRaidBlackout ? "{\"blackout\":true}" : "{\"blackout\":false}"),
                    null),
            };
        }
    }
}
