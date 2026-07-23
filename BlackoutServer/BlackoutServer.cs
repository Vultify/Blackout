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
        public override bool? IsBundleMod { get; init; } = true;

        public override SemanticVersioning.Version Version { get; init; }
            = new SemanticVersioning.Version("1.0.0", false);

        public override SemanticVersioning.Range SptVersion { get; init; }
            = new SemanticVersioning.Range("~4.0.13", false);

        public override List<string> Contributors { get; init; } = new();
        public override List<string> Incompatibilities { get; init; } = new();
        public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new()
        {
            { "com.wtt.commonlib", new SemanticVersioning.Range(">=2.0.0") },
            // Content Backport supplies the LV-119 inserts AND the Black Division clothing/voices
            { "com.wtt.contentbackport", new SemanticVersioning.Range(">=1.0.0") },
            // the Wedge and his soldiers are our own MoreBotsAPI bot types
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
        private const string Sf3pMuzzle = "69985ea146e48aa39d06a690";

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

        // the MultiCam helmet (needs its inserts pre-installed via preset) and the Wedge cover mod
        private const string ExfilMulticamHelmet = "69985ea246e48aa39d06a691";
        private const string WedgeCoverHelmet = "69985ea346e48aa39d06a692";
        // the vanilla black Team Wendy EXFIL - SPT's copy of the model has no mod_equipment_002 node
        private const string BlackExfilHelmet = "5e00c1ad86f774747333222c";
        // our black EXFIL, repacked from live 1.0.6.5 with a rewritten CAB. BSG's own mod_equipment_002
        // cover socket is baked into that bundle, so the cover only renders on this one - it hosts the slot.
        private const string WedgeBlackExfilHelmet = "69985ea646e48aa39d06a695";
        private const string CoyoteExfilHelmet = "5e01ef6886f77445f643baa4";
        // helmet-mounted headset (clones the vanilla TW EXFIL ComTac VI), not a standalone earpiece
        private const string ComTacVIBlack = "69985ea446e48aa39d06a693";
        private const string ExfilTopPlate = "6551fec55d0cf82e51014288";  // helmet_top
        private const string ExfilNapePlate = "655200ba0ef76cf7be09d528"; // helmet_back

        // Spiritus LV-119 (Icebreaker): clones the vanilla A18 rig (same donor Content Backport uses for its
        // LV-119s) and ships its own 15-pouch layout prefab in db/CustomRigLayouts/.
        private const string Lv119Rig = "69985ea546e48aa39d06a694";
        private const string Lv119Layout = "wedge_lv119";
        private const string Lv119LayoutBundle = "wedge_rig_layouts";
        private const string SoftArmorFront = "68a5f30248f18317750ab20b";
        private const string SoftArmorBack = "68a5f3a348f18317750ab4be";
        private const string BallisticPlate = "656fa53d94b480b8a500c0e4";

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

                // serves db/CustomRigLayouts/*.bundle to the client, which registers each prefab under
                // "UI/Rig Layouts/<root name>" - the name the item's RigLayoutName resolves against
                _commonLib.CustomRigLayoutService.CreateRigLayouts(Assembly.GetExecutingAssembly());

                var items = _databaseService.GetItems();

                // MP7A1/A2 accept the ARS adapter in mod_stock; the adapter accepts only the FX-KPOS stock
                AddToSlot(items, Mp7a1, "mod_stock", ArsAdapter);
                AddToSlot(items, Mp7a2, "mod_stock", ArsAdapter);
                SetSlotFilter(items, ArsAdapter, "mod_stock", FxKposStock);
                // MP7A1/A2 accept the SF3P flash hider in mod_muzzle
                AddToSlot(items, Mp7a1, "mod_muzzle", Sf3pMuzzle);
                AddToSlot(items, Mp7a2, "mod_muzzle", Sf3pMuzzle);
                // the SF3P's own mod_muzzle accepts the SOCOM556 suppressors, not the inherited MP7 Rotex.
                // Required: the bot generator hard-rewrites mod_muzzle spawn chance to 95 the moment a
                // muzzle device installs (AdjustSlotSpawnChances), so a chance-100 nested suppressor can
                // still silently miss - a required slot bypasses the roll and always resolves from the filter
                var sf3pSlot = FindSlot(items, Sf3pMuzzle, "mod_muzzle");
                if (sf3pSlot != null)
                {
                    sf3pSlot.Required = true;
                    var sf3pMuzzleFilter = sf3pSlot.Properties?.Filters?.FirstOrDefault();
                    if (sf3pMuzzleFilter != null)
                    {
                        sf3pMuzzleFilter.Filter = new HashSet<MongoId>(Socom556Suppressors.Select(id => new MongoId(id)));
                    }
                }
                // the black AN/PEQ-15 goes in every MP7 tactical slot (all three, so it mounts anywhere the
                // vanilla one does) - without this the gunsmith rejects it since it's a new backport id
                foreach (var slot in new[] { "mod_tactical_000", "mod_tactical_001", "mod_tactical_002" })
                {
                    AddToSlot(items, Mp7a1, slot, AnPeq15Black);
                    AddToSlot(items, Mp7a2, slot, AnPeq15Black);
                }

                // ship the MultiCam helmet with its soft-armor inserts pre-installed via a default preset,
                // so it isn't flagged incomplete (the top/nape slots are _required)
                var presets = _databaseService.GetGlobals().ItemPresets;
                AddArmorPreset(presets, "69985eb146e48aa39d06a6a1", "Team Wendy EXFIL MultiCam", ExfilMulticamHelmet,
                    ("helmet_top", ExfilTopPlate), ("helmet_back", ExfilNapePlate));
                AddArmorPreset(presets, "69985eb346e48aa39d06a6c1", "Spiritus LV-119 Icebreaker", Lv119Rig,
                    ("Soft_armor_front", SoftArmorFront), ("Soft_armor_back", SoftArmorBack),
                    ("Front_plate", BallisticPlate), ("Back_plate", BallisticPlate));
                AddArmorPreset(presets, "69985eb246e48aa39d06a6b1", "Team Wendy EXFIL Black", WedgeBlackExfilHelmet,
                    ("helmet_top", ExfilTopPlate), ("helmet_back", ExfilNapePlate));

                // the cover slot lives on OUR black EXFIL - only that bundle carries BSG's mod_equipment_002
                // locator, and a mod only renders on a host whose model has a node named like the slot
                AddModSlot(items, WedgeBlackExfilHelmet, "mod_equipment_002", WedgeCoverHelmet);

                // the black ComTac VI is a helmet-mounted headset - the EXFIL helmets must accept it
                // in the same slot that already takes the vanilla coyote TW EXFIL ComTac VI
                foreach (var helmet in new[] { BlackExfilHelmet, CoyoteExfilHelmet, ExfilMulticamHelmet, WedgeBlackExfilHelmet })
                {
                    AddToSlot(items, helmet, "mod_equipment_000", ComTacVIBlack);
                }

                var made = new[] { AdminKey, ArsAdapter, FxKposStock, Sf3pMuzzle, ExfilMulticamHelmet,
                    WedgeCoverHelmet, ComTacVIBlack, WedgeBlackExfilHelmet, Lv119Rig }
                    .Count(id => items.ContainsKey(new MongoId(id)));

                // single use; a wrong clone would silently inherit the donor's usage limit instead
                items.TryGetValue(new MongoId(AdminKey), out var adminKey);
                var keyUses = adminKey?.Properties?.MaximumNumberOfUsage;
                var keySellable = adminKey?.Properties?.CanSellOnRagfair;
                // prices live outside the item template, so check the tables they actually land in
                var keyHandbook = _databaseService.GetHandbook().Items?
                    .FirstOrDefault(i => i.Id == new MongoId(AdminKey))?.Price;
                _databaseService.GetPrices().TryGetValue(new MongoId(AdminKey), out var keyFlea);

                var coverSlot = SlotContains(items, WedgeBlackExfilHelmet, "mod_equipment_002", WedgeCoverHelmet);
                var comtacMounted = SlotContains(items, BlackExfilHelmet, "mod_equipment_000", ComTacVIBlack);
                // the two filter patches the gunsmith AND bot generation both depend on - assert the
                // written state, a silent FindSlot miss here is exactly how a suppressor vanishes
                var socomOnSf3p = SlotContains(items, Sf3pMuzzle, "mod_muzzle", Socom556Suppressors[0])
                    && FindSlot(items, Sf3pMuzzle, "mod_muzzle")?.Required == true;
                var peqOnMp7 = SlotContains(items, Mp7a1, "mod_tactical_000", AnPeq15Black);

                // read the rig's real state back out of the database rather than trusting the JSON
                items.TryGetValue(new MongoId(Lv119Rig), out var rig);
                var rigGrids = rig?.Properties?.Grids?.Count() ?? 0;
                var rigCells = rig?.Properties?.Grids?
                    .Sum(g => (g.Properties?.CellsH ?? 0) * (g.Properties?.CellsV ?? 0)) ?? 0;
                var rigLayout = rig?.Properties?.RigLayoutName ?? "(none)";
                var layoutRegistered = _commonLib.CustomRigLayoutService
                    .GetLayoutManifest().Contains(Lv119LayoutBundle);

                if (made == 9 && keyUses == 1 && keySellable == true && keyHandbook == 100000 && keyFlea == 157434
                    && SlotContains(items, Mp7a1, "mod_stock", ArsAdapter)
                    && SlotContains(items, Mp7a1, "mod_muzzle", Sf3pMuzzle) && coverSlot && comtacMounted
                    && socomOnSf3p && peqOnMp7
                    && rigGrids == 15 && rigCells == 28 && rigLayout == Lv119Layout && layoutRegistered)
                {
                    // everything above is asserted, not just counted - the detail only prints on failure
                    _logger.Success($"[Blackout] Custom items created ({made}/9 via WTT-CommonLib).");
                }
                else
                {
                    _logger.Error($"[Blackout] Custom items incomplete - created {made}/9, key uses {keyUses} (want 1), " +
                        $"cover slot {coverSlot}, ComTac VI {comtacMounted}, " +
                        $"SOCOM on SF3P {socomOnSf3p}, black PEQ on MP7 {peqOnMp7}, " +
                        $"LV-119 {rigGrids} pouches / {rigCells} cells, " +
                        $"layout '{rigLayout}' registered={layoutRegistered}; check CustomItems load above.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Wedge gear load failed: {ex}");
            }
        }

        // build a default preset (root item + its built-in armour in the named slots), mirroring how vanilla
        // ships armour complete - without it the required slots sit empty and the item reads as incomplete
        private static void AddArmorPreset(IDictionary<MongoId, Preset> presets, string presetId, string name,
            string rootTpl, params (string Slot, string Tpl)[] children)
        {
            var pid = new MongoId(presetId);
            if (presets.ContainsKey(pid)) return;

            var rootId = new MongoId();
            var items = new List<Item> { new() { Id = rootId, Template = new MongoId(rootTpl) } };
            foreach (var (slot, tpl) in children)
            {
                items.Add(new Item { Id = new MongoId(), Template = new MongoId(tpl), ParentId = rootId, SlotId = slot });
            }

            presets[pid] = new Preset
            {
                Id = pid,
                Type = "Preset",
                Name = $"{name} Standard",
                Parent = rootId,
                Encyclopedia = new MongoId(rootTpl),
                Items = items
            };
        }

        // add a new mod slot to a host item (standard mod-slot proto), accepting one mod - idempotent
        private static void AddModSlot(IDictionary<MongoId, TemplateItem> items, string hostId, string slotName, string modId)
        {
            if (!items.TryGetValue(new MongoId(hostId), out var host) || host.Properties?.Slots == null) return;
            var slots = host.Properties.Slots.ToList();
            if (slots.Any(s => s.Name == slotName)) return;
            slots.Add(new Slot
            {
                Name = slotName,
                Id = new MongoId(),
                Parent = hostId,
                Required = false,
                MergeSlotWithChildren = false,
                Prototype = "55d30c4c4bdc2db4468b457e",
                Properties = new SlotProperties
                {
                    Filters = new List<SlotFilter>
                    {
                        new() { Shift = 0, Filter = new HashSet<MongoId> { new MongoId(modId) } }
                    }
                }
            });
            host.Properties.Slots = slots;
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

    // Our own Black Division faction. The Wedge and his soldiers are our MoreBotsAPI types (not the
    // separate BlackDiv mod), so we register the faction ourselves. Runs at LoadFactions so it exists
    // before any hostility wiring.
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
                faction.BotTypes.Add((WildSpawnType)BlackoutBots.SoldierSpawnType);
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

    // Creates the Wedge + Black Division soldier bot types and wires their gear and hostility.
    // Soldiers use BlackDiv's shared type + loadouts (used with TacticalToaster's permission);
    // the Wedge uses our own type with our 9 backported gear items.
    [Injectable(TypePriority = MoreBotsServer.MoreBotsLoadOrder.LoadBots + 1)]
    public class BlackoutBots : IOnLoad
    {
        public const int WedgeSpawnType = 868588;
        public const int SoldierSpawnType = 868589;
        public const string WedgeName = "bossWedge";
        public const string SoldierName = "blackDivAssault";

        private const string ArmoryGuid = "com.wtt.armory";
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

                // soldiers from BlackDiv's shared type; the Wedge from our own type file
                await _moreBots.LoadBotsShared(assembly, "blackDiv", new List<string> { SoldierName });
                await _moreBots.LoadBots(assembly);

                // the ScavRole -> display-name locales (db/CustomLocales), so the kill screen reads
                // "The Wedge" / "Black Division" instead of the raw ScavRole key
                await _commonLib.CustomLocaleService.CreateCustomLocales(assembly, null);

                // the Wedge's own head/top/pants/voice (his ripped-live bundles), so he looks and
                // sounds like himself rather than a generic Black Division leader
                await _commonLib.CustomHeadService.CreateCustomHeads(assembly, null);
                await _commonLib.CustomClothingService.CreateCustomClothing(assembly, null);
                await _commonLib.CustomVoiceService.CreateCustomVoices(assembly, null);

                _customBotTypeService.AddCustomWildSpawnTypeNames(new Dictionary<int, string>
                {
                    { WedgeSpawnType, WedgeName },
                    { SoldierSpawnType, SoldierName },
                });

                // register how many of each to pre-generate per batch, else BotController warns
                // "Unable to find a preset count" and falls back to 30. Wedge is a lone boss (few);
                // the soldiers spawn in respawning waves, so cache a fuller batch like assault does.
                var presetBatch = _configServer.GetConfig<BotConfig>().PresetBatch;
                presetBatch[WedgeName] = 5;
                presetBatch[SoldierName] = 30;

                // base loadouts always (soldier weapons that exist without Armory); the Armory
                // arsenal only when WTT-Armory is installed - BlackDiv's own graceful-degrade pattern
                await _commonLib.CustomBotLoadoutService.CreateCustomBotLoadouts(assembly, null);
                var hasArmory = _modList.Any(m => m.ModMetadata.ModGuid == ArmoryGuid);
                if (hasArmory)
                {
                    await _commonLib.CustomBotLoadoutService.CreateCustomBotLoadouts(
                        assembly, System.IO.Path.Combine("db", "ModBotLoadouts", "Armory"));
                }

                // hostility, both directions explicitly (the API's own consumers always do)
                var mine = new[] { WedgeName, SoldierName };
                foreach (var faction in EnemyFactions)
                {
                    _factionService.AddEnemyByFaction(mine, faction);
                    _factionService.AddEnemyByFaction(faction, BlackoutFaction.FactionName);
                }

                // read the types back out of the database rather than trusting the calls
                var bots = _databaseService.GetBots().Types;
                var wedgeOk = bots.TryGetValue(WedgeName.ToLowerInvariant(), out var wedge);
                var soldierOk = bots.ContainsKey(SoldierName.ToLowerInvariant());

                // the Wedge's own head/top/pants/voice must actually be in the customization DB, else
                // he spawns with a broken/missing appearance and nothing logs it
                var cust = _databaseService.GetCustomization();
                string[] wedgeAppearance = { "69985f0146e48aa39d06a701", "69985f0246e48aa39d06a702",
                    "69985f0346e48aa39d06a703", "69985f0446e48aa39d06a704" };
                var appearanceOk = wedgeAppearance.Count(id => cust.ContainsKey(new MongoId(id)));
                var wedgeHp = wedgeOk
                    ? (int)(wedge!.BotHealth?.BodyParts?.FirstOrDefault() is { } bp
                        ? bp.Chest.Max + bp.Head.Max + bp.LeftArm.Max + bp.LeftLeg.Max
                          + bp.RightArm.Max + bp.RightLeg.Max + bp.Stomach.Max
                        : 0)
                    : 0;

                if (wedgeOk && soldierOk && appearanceOk == 4)
                {
                    _logger.Success($"[Blackout] Bots loaded: Wedge ('{WedgeName}', {wedgeHp} HP, own appearance) + soldiers " +
                        $"('{SoldierName}'); Armory loadout: {(hasArmory ? "on" : "off (base weapons)")}; " +
                        $"hostile to {EnemyFactions.Length} factions.");
                }
                else
                {
                    _logger.Error($"[Blackout] Bot load incomplete - Wedge={wedgeOk}, soldier={soldierOk}, " +
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

    // Places the Wedge and Black Division on Labs as timed waves. Spawn data bakes into the location
    // at generation time, so it is injected here and re-injected after every raid.
    [Injectable(InjectionType.Singleton)]
    public class BlackoutSpawnController
    {
        // Labs bot zones. BOTH gate zones (Gate1 z~-225, Gate2 z~-451) are sealed ramps at y~2.7 behind
        // the power-gated doors - only Floor1/Floor2 are in the map's OpenZones. Bots in either gate zone
        // get trapped, so Wedge and the squad each hold one open floor; waves roam both. Gates/basement unused.
        private const string WedgeZone = "BotZoneFloor2";
        private const string SquadZone = "BotZoneFloor1";
        private const string WaveZones = "BotZoneFloor1,BotZoneFloor2";
        private const string WedgeEscorts = "4,4,5,5"; // Wedge + 4-5 guards
        private const string SquadEscorts = "3,3,4,4"; // a soldier lead + 3-4 = 4-5 per squad
        private const int WaveIntervalSec = 120;
        private const int WaveStopBeforeEndSec = 180;

        private readonly DatabaseService _databaseService;
        private readonly ISptLogger<BlackoutSpawnController> _logger;

        public BlackoutSpawnController(DatabaseService databaseService, ISptLogger<BlackoutSpawnController> logger)
        {
            _databaseService = databaseService;
            _logger = logger;
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
            // ours are identified by boss type - the base Labs map only spawns pmcBot as bosses,
            // so removing our two types is safe and makes re-injection idempotent
            spawns.RemoveAll(s => s.BossName == BlackoutBots.WedgeName || s.BossName == BlackoutBots.SoldierName);

            // Wedge leads 4-5 Black Division guards on one open floor, raid start
            spawns.Add(Squad(BlackoutBots.WedgeName, BlackoutBots.SoldierName, WedgeEscorts, WedgeZone, time: -1, ignoreCap: true));
            // a 4-5 Black Division squad on the other open floor (a soldier leads), raid start
            spawns.Add(Squad(BlackoutBots.SoldierName, BlackoutBots.SoldierName, SquadEscorts, SquadZone, time: -1, ignoreCap: false));

            // waves of Black Division across the map (client picks a zone from the list = never basement),
            // one squad every couple minutes; they respect the bot cap so they throttle instead of piling up
            var raidSec = (labs.Base.EscapeTimeLimit ?? 35) * 60;
            var count = 0;
            for (var t = WaveIntervalSec; t < raidSec - WaveStopBeforeEndSec; t += WaveIntervalSec)
            {
                spawns.Add(Squad(BlackoutBots.SoldierName, BlackoutBots.SoldierName, SquadEscorts, WaveZones, time: t, ignoreCap: false));
                count++;
            }

            labs.Base.BossLocationSpawn = spawns;
            return count;
        }

        // bossType leads a group of escortType. Wedge groups have BossName=bossWedge; pure squads
        // use a soldier as the nominal boss so the group is all Black Division.
        private static BossLocationSpawn Squad(string bossType, string escortType, string amount,
            string zone, int time, bool ignoreCap)
        {
            return new BossLocationSpawn
            {
                BossName = bossType,
                BossChance = 100,
                BossDifficulty = "normal",
                BossEscortType = escortType,
                BossEscortAmount = amount,
                BossEscortDifficulty = "normal",
                BossZone = zone,
                Time = time,
                IsBossPlayer = false,
                IsRandomTimeSpawn = false,
                Delay = 0,
                ForceSpawn = false,
                IgnoreMaxBots = ignoreCap,
                SpawnMode = ["regular", "pve"],
            };
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
                var waves = _controller.Inject();
                _logger.Success($"[Blackout] Black Division on Labs: Wedge + gate squad at raid start, {waves} timed waves.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Spawn injection failed: {ex}");
            }
            return Task.CompletedTask;
        }
    }

    // Spawn data is rebuilt per raid, so re-inject once the previous one ends.
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
}
