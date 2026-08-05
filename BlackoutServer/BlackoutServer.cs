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
        public override bool? IsBundleMod { get; init; } = false;

        public override SemanticVersioning.Version Version { get; init; }
            = new SemanticVersioning.Version("3.1.0", false);

        public override SemanticVersioning.Range SptVersion { get; init; }
            = new SemanticVersioning.Range("~4.0.13", false);

        public override List<string> Contributors { get; init; } = new();
        public override List<string> Incompatibilities { get; init; } = new();
        public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new()
        {
            // creates the Admin's key. the only dependency left now the Black Division is gone
            { "com.wtt.commonlib", new SemanticVersioning.Range(">=2.0.22") },
        };
    }

    // The event's Admin's key (db/CustomItems/blackout_key.json), created through WTT-CommonLib.
    // The only item the mod adds. Runs post-DB so the clone donor already exists.
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 4)]
    public class BlackoutCustomItems : IOnLoad
    {
        // the live event's Admin's key, real 1.0.6.5 item id - opens the system admin office
        private const string AdminKey = "6a33c17933cff6b88c08902e";

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

                // the Admin's key is the only item Blackout adds
                var made = items.ContainsKey(new MongoId(AdminKey)) ? 1 : 0;

                // single use; a wrong clone would silently inherit the donor's usage limit instead
                items.TryGetValue(new MongoId(AdminKey), out var adminKey);
                var keyUses = adminKey?.Properties?.MaximumNumberOfUsage;
                var keySellable = adminKey?.Properties?.CanSellOnRagfair;
                // prices live outside the item template, so check the tables they actually land in
                var keyHandbook = _databaseService.GetHandbook().Items?
                    .FirstOrDefault(i => i.Id == new MongoId(AdminKey))?.Price;
                _databaseService.GetPrices().TryGetValue(new MongoId(AdminKey), out var keyFlea);

                if (made != 1 || keyUses != 1 || keySellable != true
                    || keyHandbook != 100000 || keyFlea != 157434)
                {
                    _logger.Error($"[Blackout] Admin key incomplete - created {made}/1, uses {keyUses} (want 1), " +
                        $"sellable {keySellable}, handbook {keyHandbook} (want 100000), flea {keyFlea} (want 157434).");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Admin key load failed: {ex}");
            }
        }
    }

    // Owns the per-raid coin flip. Rolled server-side and re-rolled after every raid, so the darkness,
    // lockdown, keypads and the locked arsenal door all ride the same result.
    [Injectable(InjectionType.Singleton)]
    public class BlackoutSpawnController
    {
        private const double DefaultChance = 25;

        private readonly DatabaseService _databaseService;
        private readonly RandomUtil _randomUtil;
        private readonly ISptLogger<BlackoutSpawnController> _logger;
        // reloaded before every roll, not just at startup, so editing config.json between raids takes
        // effect without restarting the server (which in practice meant closing the game). Seeded with
        // the default so the first read has something to fall back to
        private double _chance = DefaultChance;

        // the server owns the coin flip so every client in the raid agrees on it - they read the
        // result off /blackout/state
        public bool CurrentRaidBlackout { get; private set; }

        // and the emergency code with it. the client used to roll its own, which is fine for one player
        // and useless for several - in a co-op raid every player got a different number on the same
        // whiteboard, and each keypad only accepted the code that client happened to generate
        public string CurrentRaidCode { get; private set; } = "0000";

        public BlackoutSpawnController(DatabaseService databaseService, RandomUtil randomUtil, ISptLogger<BlackoutSpawnController> logger)
        {
            _databaseService = databaseService;
            _randomUtil = randomUtil;
            _logger = logger;
            _chance = LoadChance();
            _logger.Success($"[Blackout] blackout chance {_chance}% per Labs raid (from config.json).");
        }

        // config.json sits next to our dll, copy-if-missing on install so a player's edit survives updates.
        // Returns the current value on any failure rather than the default - a read that lands mid-save
        // would otherwise silently drop a 100% config back to 25 for the next raid
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
                        return Math.Clamp(v.GetDouble(), 0, 100);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Blackout] config.json unreadable, staying on {_chance}%: {ex.Message}");
            }
            return _chance;
        }

        // Rolls this raid. Nothing is injected into the location any more - the roll is the whole job.
        public void Roll()
        {
            // pick up a config edit made since the last raid. Only says anything when the number
            // actually moved, so a normal raid stays quiet but your edit confirms itself
            var chance = LoadChance();
            if (chance != _chance)
            {
                _logger.Success($"[Blackout] blackout chance changed to {chance}% (was {_chance}%).");
                _chance = chance;
            }

            // a failed roll leaves Labs completely vanilla - the client reads the same result and skips
            // the darkness, lockdown, keypads and the locked door
            CurrentRaidBlackout = _randomUtil.GetChance100(_chance);
            // one code per raid, rolled here so every client in it reads the same four digits
            CurrentRaidCode = _randomUtil.GetInt(0, 9999).ToString("D4");

            PlaceArsenalKey(CurrentRaidBlackout);

            // no raid code in the log on purpose - the whiteboard is where you're meant to find it
            _logger.Info(CurrentRaidBlackout
                ? "[Blackout] Roll: BLACKOUT this raid."
                : "[Blackout] Roll: normal Labs this raid.");
        }

        // The arsenal key sits on the boss's desk in the manager's office, which is itself behind the
        // vanilla manager's office key - so the arsenal is two keys deep rather than a free grab.
        // Forced, not loot-table odds: the key gates the whole event payoff, and a blackout raid where
        // it happened not to roll is a dead end.
        private const string ArsenalKeyTpl = "6a33c17933cff6b88c08902e";
        private const string ArsenalKeySpawnId = "blackout_arsenal_key";
        // read off Lab_recreation_bosstable_COLLIDER, lifted 2cm so it rests on the desk not in it
        private static readonly (double X, double Y, double Z) DeskPoint = (-162.978, 4.958, -347.554);

        private void PlaceArsenalKey(bool blackout)
        {
            var labs = _databaseService.GetLocations().Laboratory;
            if (labs?.LooseLoot == null)
            {
                _logger.Error("[Blackout] Labs loose loot unavailable - arsenal key not placed.");
                return;
            }

            labs.LooseLoot.AddTransformer(loot =>
            {
                if (loot == null)
                {
                    return loot;
                }

                var forced = loot.SpawnpointsForced?.ToList() ?? new List<Spawnpoint>();
                // drop any previous copy first so re-rolling between raids can't stack duplicates
                forced.RemoveAll(s => s.Template?.Id == ArsenalKeySpawnId);

                if (blackout)
                {
                    var itemId = new MongoId();
                    forced.Add(new Spawnpoint
                    {
                        LocationId = $"({DeskPoint.X}, {DeskPoint.Y}, {DeskPoint.Z})",
                        Probability = 1,
                        Template = new SpawnpointTemplate
                        {
                            Id = ArsenalKeySpawnId,
                            IsContainer = false,
                            UseGravity = false,
                            RandomRotation = false,
                            Position = new XYZ { X = DeskPoint.X, Y = DeskPoint.Y, Z = DeskPoint.Z },
                            Rotation = new XYZ { X = 0, Y = 0, Z = 0 },
                            IsGroupPosition = false,
                            GroupPositions = new List<GroupPosition>(),
                            IsAlwaysSpawn = true,
                            Root = itemId.ToString(),
                            Items = new List<SptLootItem>
                            {
                                new SptLootItem
                                {
                                    Id = itemId,
                                    Template = new MongoId(ArsenalKeyTpl),
                                    Upd = new Upd { StackObjectsCount = 1 },
                                },
                            },
                        },
                    });
                }

                loot.SpawnpointsForced = forced;
                return loot;
            });
        }
    }

    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 5)]
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
                _controller.Roll();
            }
            catch (Exception ex)
            {
                _logger.Error($"[Blackout] Raid roll failed: {ex}");
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
                        _controller.Roll();
                        return await new ValueTask<object>(output ?? string.Empty);
                    }, null),
            };
        }
    }

    // Tells the client whether THIS raid rolled a blackout, so the darkness, the extract lockdown, the
    // keypads and the locked arsenal door all ride the same flip instead of rolling apart.
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
                            "{\"blackout\":" + (_controller.CurrentRaidBlackout ? "true" : "false")
                            + ",\"code\":\"" + _controller.CurrentRaidCode + "\"}"),
                    null),
            };
        }
    }
}
