using System.Reflection;
using MoreBotsServer;
using MoreBotsServer.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Scourge.Server;

[Injectable(TypePriority = OnLoadOrder.Preload + 20)]
public sealed class ScourgeServer(
    MoreBotsAPI moreBotsApi,
    MoreBotsCustomBotTypeService customBotTypeService,
    BotTable botTable,
    LocationTable locationTable,
    TemplateTable templateTable,
    SeasonalEventConfig seasonalEventConfig,
    ISptLogger<ScourgeServer> logger) : IOnLoad
{
    private static readonly MongoId MissingGasGrenade = new("69f07893e761ac1c200ae15b");
    private static readonly MongoId InstalledGasGrenade = new("6a5d6a5f4ed8c025a0a2cff0");
    private static readonly MongoId ScourgeSmartphone = new("6ac4cfbc99bdd7779608792f");
    private static readonly MongoId MissingScourgeVoice = new("6acf6e6e35ee26cb5b0a5ab1");
    private static readonly MongoId FallbackVoice = new("646e46ca286188c5480a1a37");
    private ScourgeConfig? _config;
    private bool _bpsPatched;

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assembly = Assembly.GetExecutingAssembly();
        await moreBotsApi.LoadBots(assembly);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureBotTypesLoaded();
        AdaptBotData();
        customBotTypeService.AddCustomWildSpawnTypeNames(new Dictionary<int, string>
        {
            // MoreBots uses this fallback directly as a BotTable key when the client enum
            // has no generated name, so keep these values lowercase like its type loader.
            [ScourgeRoles.BossValue] = ScourgeRoles.Boss.ToLowerInvariant(),
            [ScourgeRoles.FollowerValue] = ScourgeRoles.Follower.ToLowerInvariant(),
        });
        RegisterEventBotMappings();

        var config = _config = ScourgeConfig.Load(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly.Location)!, "config.json"));
        if (!config.Enabled)
        {
            logger.Info("[Scourge] event disabled by config");
            return;
        }

        EnsureLabsWave();
        EnableBpsCompatibility();
        logger.Info($"[Scourge] registered {ScourgeRoles.Boss} on Labs ({config.SpawnChance:0}% chance, {config.EscortCount} escorts, zones: {string.Join(',', config.SpawnZones)})");
    }

    private void RegisterEventBotMappings()
    {
        // SPT 4.1 treats any role containing "event" as an event bot and resolves
        // it through EventBotMapping before generating its template. These custom
        // roles are absent from the seasonal data, so install their self-mappings.
        seasonalEventConfig.EventBotMapping[ScourgeRoles.Boss] = ScourgeRoles.Boss;
        seasonalEventConfig.EventBotMapping[ScourgeRoles.Follower] = ScourgeRoles.Follower;
        seasonalEventConfig.EventBotMapping[ScourgeRoles.Boss.ToLowerInvariant()] = ScourgeRoles.Boss;
        seasonalEventConfig.EventBotMapping[ScourgeRoles.Follower.ToLowerInvariant()] = ScourgeRoles.Follower;
    }

    private void EnsureLabsWave()
    {
        if (_config is not { Enabled: true } config)
        {
            return;
        }

        var laboratory = locationTable.Laboratory?.Base
            ?? throw new InvalidOperationException("[Scourge] Labs location data is unavailable.");
        if (laboratory.BossLocationSpawn is null)
        {
            throw new InvalidOperationException("[Scourge] Labs boss spawn list is unavailable.");
        }

        var availableZones = (laboratory.SpawnPointParams ?? [])
            .Select(point => point.BotZoneName)
            .Where(zone => !string.IsNullOrWhiteSpace(zone))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var zone in config.SpawnZones)
        {
            if (!availableZones.Contains(zone))
            {
                throw new InvalidDataException($"[Scourge] configured Labs spawn zone '{zone}' does not exist in this map's spawn data.");
            }
        }

        if (!ContainsScourgeWave(laboratory.BossLocationSpawn))
        {
            laboratory.BossLocationSpawn.Add(BuildWave(config));
        }

    }

    private void EnableBpsCompatibility()
    {
        if (_bpsPatched) return;
        var bpsType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("BotPlacementSystemServer.Controllers.MapSpawns", false))
            .FirstOrDefault(type => type is not null);
        if (bpsType is null) return;

        foreach (var methodName in new[] { "ReplaceOriginalLocations", "ReplaceOriginalLocationForMap" })
        {
            var method = bpsType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException($"[Scourge] BPS compatibility target '{methodName}' is unavailable.");
            new BpsSpawnRestorePatch(method, EnsureLabsWave).Enable();
        }
        _bpsPatched = true;
        logger.Info("[Scourge] BPS compatibility enabled: the Labs squad is restored after spawn-list rebuilds.");
    }

    private void EnsureBotTypesLoaded()
    {
        EnsureBotType(ScourgeRoles.Boss);
        EnsureBotType(ScourgeRoles.Follower);
    }

    private void EnsureBotType(string role)
    {
        if (!botTable.Types.TryGetValue(role.ToLowerInvariant(), out var bot) || bot is null)
        {
            throw new InvalidOperationException($"[Scourge] MoreBots did not load bot role '{role}'. Check db/bots/types and the MoreBots dependency.");
        }

        if (bot.BotInventory is null || bot.BotAppearance is null)
        {
            throw new InvalidOperationException($"[Scourge] bot role '{role}' has incomplete inventory or appearance data.");
        }
    }

    private void AdaptBotData()
    {
        AdaptBot(ScourgeRoles.Boss);
        AdaptBot(ScourgeRoles.Follower);
    }

    private void AdaptBot(string role)
    {
        var bot = botTable.Types[role.ToLowerInvariant()]!;
        var inventory = bot.BotInventory;

        AdaptItemPools(inventory.Items);
        ValidateEquipment(inventory, role);
        ValidateAmmo(inventory, role);
        ValidateMods(inventory, role);
        AdaptVoice(bot, role);
        ValidateAppearance(bot, role);
    }

    private void AdaptItemPools(ItemPools? pools)
    {
        if (pools is null)
        {
            return;
        }

        RemapOrRemove(pools.Backpack);
        RemapOrRemove(pools.Pockets);
        RemapOrRemove(pools.SecuredContainer);
        RemapOrRemove(pools.SpecialLoot);
        RemapOrRemove(pools.TacticalVest);
    }

    private void RemapOrRemove(Dictionary<MongoId, double>? pool)
    {
        if (pool is null)
        {
            return;
        }

        if (!templateTable.Items.ContainsKey(MissingGasGrenade) && pool.Remove(MissingGasGrenade, out var gasWeight))
        {
            if (templateTable.Items.ContainsKey(InstalledGasGrenade))
            {
                pool[InstalledGasGrenade] = pool.TryGetValue(InstalledGasGrenade, out var existing) ? existing + gasWeight : gasWeight;
            }
        }

        pool.Remove(ScourgeSmartphone);
        RemoveUnavailable(pool, templateTable.Items);
    }

    private void ValidateEquipment(BotTypeInventory inventory, string role)
    {
        if (inventory.Equipment is null)
        {
            throw new InvalidOperationException($"[Scourge] bot role '{role}' has no equipment pools.");
        }

        foreach (var slot in inventory.Equipment.Keys.ToArray())
        {
            var pool = inventory.Equipment[slot];
            ValidateItemChoices(pool.Keys, role, $"equipment/{slot}");
            if ((slot.ToString() is "FirstPrimaryWeapon" or "TacticalVest") && (pool is null || pool.Count == 0))
            {
                throw new InvalidOperationException($"[Scourge] bot role '{role}' has no installed item for required slot '{slot}'.");
            }
        }
    }

    private void ValidateAmmo(BotTypeInventory inventory, string role)
    {
        if (inventory.Ammo is null)
        {
            throw new InvalidOperationException($"[Scourge] '{role}' has no ammunition pools.");
        }

        foreach (var caliber in inventory.Ammo.Keys.ToArray())
        {
            ValidateItemChoices(inventory.Ammo[caliber].Keys, role, $"ammo/{caliber}");
            if (inventory.Ammo[caliber].Count == 0)
            {
                throw new InvalidOperationException($"[Scourge] '{role}' has an empty ammunition pool for {caliber}.");
            }
        }
    }

    private void ValidateMods(BotTypeInventory inventory, string role)
    {
        if (inventory.Mods is null)
        {
            return;
        }

        foreach (var weapon in inventory.Mods.Keys.ToArray())
        {
            ValidateItemChoices([weapon], role, "attachment parent");

            var slots = inventory.Mods[weapon];
            foreach (var slot in slots.Keys.ToArray())
            {
                ValidateItemChoices(slots[slot], role, $"mods/{weapon}/{slot}");
            }
        }
    }

    private void ValidateItemChoices(IEnumerable<MongoId> choices, string role, string pool)
    {
        foreach (var id in choices)
        {
            if (!templateTable.Items.ContainsKey(id))
            {
                throw new InvalidOperationException($"[Scourge] '{role}' requires missing item '{id}' in {pool}. Check WTT ContentBackport and its load order.");
            }
        }
    }

    private void AdaptVoice(BotType bot, string role)
    {
        var voices = bot.BotAppearance.Voice;
        if (voices is null)
        {
            bot.BotAppearance.Voice = new Dictionary<MongoId, double>();
            voices = bot.BotAppearance.Voice;
        }

        if (!templateTable.Customization.ContainsKey(MissingScourgeVoice) && voices.Remove(MissingScourgeVoice, out var missingVoiceWeight))
        {
            if (templateTable.Customization.ContainsKey(FallbackVoice))
            {
                voices[FallbackVoice] = voices.TryGetValue(FallbackVoice, out var existing) ? existing + missingVoiceWeight : missingVoiceWeight;
            }
        }

        RemoveUnavailable(voices, templateTable.Customization);
        if (voices.Count == 0)
        {
            if (!templateTable.Customization.ContainsKey(FallbackVoice))
            {
                throw new InvalidOperationException($"[Scourge] bot role '{role}' has no valid voice customization after adapting the missing Scourge voice.");
            }

            voices[FallbackVoice] = 1;
        }
    }

    private void ValidateAppearance(BotType bot, string role)
    {
        ValidateCustomization(bot.BotAppearance.Body, role, "body");
        ValidateCustomization(bot.BotAppearance.Feet, role, "feet");
        ValidateCustomization(bot.BotAppearance.Hands, role, "hands");
        ValidateCustomization(bot.BotAppearance.Head, role, "head");
        ValidateCustomization(bot.BotAppearance.Voice, role, "voice");
    }

    private void ValidateCustomization(Dictionary<MongoId, double>? choices, string role, string part)
    {
        if (choices is null || choices.Count == 0)
        {
            throw new InvalidOperationException($"[Scourge] bot role '{role}' has no '{part}' customization choices.");
        }

        foreach (var id in choices.Keys)
        {
            if (!templateTable.Customization.ContainsKey(id))
            {
                throw new InvalidOperationException($"[Scourge] bot role '{role}' references missing {part} customization '{id}'.");
            }
        }
    }

    private static void RemoveUnavailable<T>(Dictionary<MongoId, double>? choices, IReadOnlyDictionary<MongoId, T> available)
    {
        if (choices is null)
        {
            return;
        }

        foreach (var id in choices.Keys.ToArray())
        {
            if (!available.ContainsKey(id))
            {
                choices.Remove(id);
            }
        }
    }

    private static BossLocationSpawn BuildWave(ScourgeConfig config) => new()
    {
        BossChance = config.SpawnChance,
        BossDifficulty = "hard",
        BossEscortAmount = config.EscortCount.ToString(),
        BossEscortDifficulty = "hard",
        BossEscortType = ScourgeRoles.Follower,
        BossName = ScourgeRoles.Boss,
        IsBossPlayer = false,
        BossZone = string.Join(',', config.SpawnZones),
        Delay = 0,
        IgnoreMaxBots = false,
        IsRandomTimeSpawn = false,
        ShowOnTarkovMap = false,
        ShowOnTarkovMapPvE = false,
        SpawnMode = ["regular", "pve"],
        Supports = [],
        Time = config.SpawnTime,
        TriggerId = string.Empty,
        TriggerName = string.Empty,
    };

    private static bool ContainsScourgeWave(IEnumerable<BossLocationSpawn> waves)
    {
        foreach (var wave in waves)
        {
            if (string.Equals(wave.BossName, ScourgeRoles.Boss, StringComparison.OrdinalIgnoreCase)
                && string.Equals(wave.BossEscortType, ScourgeRoles.Follower, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
