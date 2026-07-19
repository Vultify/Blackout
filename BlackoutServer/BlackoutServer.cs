using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
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
        public override bool? IsBundleMod { get; init; } = false;

        public override SemanticVersioning.Version Version { get; init; }
            = new SemanticVersioning.Version("1.0.0", false);

        public override SemanticVersioning.Range SptVersion { get; init; }
            = new SemanticVersioning.Range("~4.0.13", false);

        public override List<string> Contributors { get; init; } = new();
        public override List<string> Incompatibilities { get; init; } = new();
        public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new();
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
}
