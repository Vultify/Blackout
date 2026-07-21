using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;

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
            // the LV-119 rig clones Content Backport's, and uses its soft-armour inserts
            { "com.wtt.contentbackport", new SemanticVersioning.Range(">=1.0.0") },
        };
    }

    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 3)]
    public class BlackoutServerMod : IOnLoad
    {
        // the live event's Admin's key, real 1.0.6.5 item id - opens the system admin office
        public const string AdminKeyId = "6a33c17933cff6b88c08902e";
        private const string DonorArsenalKey = "5c1f79a086f7746ed066fb8f";
        private const string KeyMechanicalParent = "5c99f98d86f7745c314214b3";
        private const string KeysHandbookParent = "5c518ec986f7743b68682ce2";

        private readonly CustomItemService _customItemService;
        private readonly DatabaseService _databaseService;
        private readonly ISptLogger<BlackoutServerMod> _logger;

        public BlackoutServerMod(
            CustomItemService customItemService,
            DatabaseService databaseService,
            ISptLogger<BlackoutServerMod> logger)
        {
            _customItemService = customItemService;
            _databaseService = databaseService;
            _logger = logger;
        }

        public Task OnLoad()
        {
            try
            {
                _customItemService.CreateItemFromClone(new NewItemFromCloneDetails
                {
                    ItemTplToClone = DonorArsenalKey,
                    NewId = AdminKeyId,
                    ParentId = KeyMechanicalParent,
                    HandbookParentId = KeysHandbookParent,
                    HandbookPriceRoubles = 150000,
                    Locales = new Dictionary<string, LocaleDetails>
                    {
                        ["en"] = new LocaleDetails
                        {
                            Name = "Admin's key",
                            ShortName = "Admin",
                            Description =
                                "A mechanical key that unlocks either a utility room or one of the " +
                                "system administrators' offices.",
                        }
                    },
                    OverrideProperties = new TemplateItemProperties
                    {
                        MaximumNumberOfUsage = 0,
                        CanSellOnRagfair = false,
                    }
                });

                // verify the item actually landed in the database, not just that the call returned
                if (_databaseService.GetItems().TryGetValue(new MongoId(AdminKeyId), out var created))
                {
                    _logger.Success($"[Blackout] Key created: {created.Id} ({created.Properties?.MaximumNumberOfUsage} uses = infinite)");
                }
                else
                {
                    _logger.Error("[Blackout] Key creation FAILED - id not present in item database");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Server OnLoad failed: {ex}");
            }

            return Task.CompletedTask;
        }
    }

    // The Wedge's gear, repacked from live-EFT bundles and created via WTT-CommonLib
    // (db/CustomItems/wedge_gear.json). Runs post-DB so the clone donors already exist.
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 4)]
    public class BlackoutWedgeGear : IOnLoad
    {
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
        private readonly ISptLogger<BlackoutWedgeGear> _logger;

        public BlackoutWedgeGear(
            WTTServerCommonLib.WTTServerCommonLib commonLib,
            DatabaseService databaseService,
            ISptLogger<BlackoutWedgeGear> logger)
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
                // the SF3P's own mod_muzzle accepts the SOCOM556 suppressors, not the inherited MP7 Rotex
                var sf3pMuzzleSlot = FindSlot(items, Sf3pMuzzle, "mod_muzzle")?.Properties?.Filters?.FirstOrDefault();
                if (sf3pMuzzleSlot != null)
                {
                    sf3pMuzzleSlot.Filter = new HashSet<MongoId>(Socom556Suppressors.Select(id => new MongoId(id)));
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

                var made = new[] { ArsAdapter, FxKposStock, Sf3pMuzzle, ExfilMulticamHelmet,
                    WedgeCoverHelmet, ComTacVIBlack, WedgeBlackExfilHelmet, Lv119Rig }
                    .Count(id => items.ContainsKey(new MongoId(id)));

                var coverSlot = SlotContains(items, WedgeBlackExfilHelmet, "mod_equipment_002", WedgeCoverHelmet);
                var comtacMounted = SlotContains(items, BlackExfilHelmet, "mod_equipment_000", ComTacVIBlack);

                // read the rig's real state back out of the database rather than trusting the JSON
                items.TryGetValue(new MongoId(Lv119Rig), out var rig);
                var rigGrids = rig?.Properties?.Grids?.Count() ?? 0;
                var rigCells = rig?.Properties?.Grids?
                    .Sum(g => (g.Properties?.CellsH ?? 0) * (g.Properties?.CellsV ?? 0)) ?? 0;
                var rigLayout = rig?.Properties?.RigLayoutName ?? "(none)";
                var layoutRegistered = _commonLib.CustomRigLayoutService
                    .GetLayoutManifest().Contains(Lv119LayoutBundle);

                if (made == 8 && SlotContains(items, Mp7a1, "mod_stock", ArsAdapter)
                    && SlotContains(items, Mp7a1, "mod_muzzle", Sf3pMuzzle) && coverSlot && comtacMounted
                    && rigGrids == 15 && rigCells == 28 && rigLayout == Lv119Layout && layoutRegistered)
                {
                    _logger.Success($"[Blackout] Wedge gear created ({made}/8); MP7 slots wired; " +
                        $"cover slot on custom black EXFIL: {coverSlot}; ComTac VI on EXFIL: {comtacMounted}; " +
                        $"LV-119 {rigGrids} pouches / {rigCells} cells, layout '{rigLayout}' " +
                        $"(bundle registered: {layoutRegistered}).");
                }
                else
                {
                    _logger.Error($"[Blackout] Wedge gear incomplete - created {made}/8, cover slot {coverSlot}, " +
                        $"ComTac VI {comtacMounted}, LV-119 {rigGrids} pouches / {rigCells} cells, " +
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
}
