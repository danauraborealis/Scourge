using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.InRaid;

namespace Manimal.Scourge.Server;

/// <summary>
/// Gives Scourge's generated Labs squad the optional RDA-78 vaccine grenade when
/// MapVariants is serving the Halloween infected Labs variant.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class ScourgeInfectedLabsGas(
    IServiceProvider serviceProvider,
    TemplateTable templateTable,
    ISptLogger<ScourgeInfectedLabsGas> logger)
{
    private const string Laboratory = "laboratory";
    private const string InfectedVariant = "backport";
    private const string HalloweenInfectedLabsType = "Manimal.Halloween2026.Server.InfectedLabs";
    private const string HalloweenConfigType = "Manimal.Halloween2026.Server.HalloweenConfig";

    private static readonly MongoId MissingGasGrenade = new("69f07893e761ac1c200ae15b");
    private static readonly MongoId CsGasGrenade = new("6a5d6a5f4ed8c025a0a2cff0");
    private static readonly MongoId VaccineGasGrenade = new("6a9854955358fa10380b8509");

    private readonly ConcurrentDictionary<MongoId, bool> _infectedLabsBySession = new();
    private readonly object _mapVariantsLock = new();
    private bool _enabled;
    private volatile bool _mapVariantsBound;
    private object? _mapVariantsProviders;
    private MethodInfo? _installedVariant;
    private int _warnedMissingVaccine;

    internal void EnableIfAvailable()
    {
        if (_enabled)
        {
            return;
        }

        new LocationSnapshotPatch(this).Enable();
        new BotLoadoutPatch(this).Enable();
        _enabled = true;
        logger.Info("[Scourge] infected Labs gas loadout compatibility enabled.");
    }

    private void RememberLocation(MongoId sessionId, string? location)
    {
        if (!string.Equals(location, Laboratory, StringComparison.OrdinalIgnoreCase))
        {
            _infectedLabsBySession[sessionId] = false;
            return;
        }

        _infectedLabsBySession[sessionId] = IsInfectedLabsVariant();
    }

    private bool IsInfectedLabs(MongoId sessionId, string? location)
    {
        return string.Equals(location, Laboratory, StringComparison.OrdinalIgnoreCase)
            && _infectedLabsBySession.TryGetValue(sessionId, out var infected)
            && infected;
    }

    private bool IsInfectedLabsVariant()
    {
        try
        {
            var halloweenAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetType(HalloweenInfectedLabsType, throwOnError: false) is not null);
            if (halloweenAssembly is null || !IsInfectedLabsEnabled(halloweenAssembly))
            {
                return false;
            }

            if (!BindMapVariants())
            {
                return false;
            }

            var variant = _installedVariant!.Invoke(_mapVariantsProviders, [Laboratory]) as string;
            return string.Equals(variant, InfectedVariant, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger.Debug($"[Scourge] could not inspect the installed Labs variant: {exception.Message}");
            return false;
        }
    }

    private bool IsInfectedLabsEnabled(Assembly halloweenAssembly)
    {
        var configType = halloweenAssembly.GetType(HalloweenConfigType, throwOnError: false);
        if (configType is null)
        {
            return false;
        }

        var config = serviceProvider.GetService(configType);
        if (config is null)
        {
            return false;
        }

        var settings = configType.GetProperty("Settings", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
        var enabled = settings?.GetType().GetProperty("InfectedLabsEnabled", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(settings);
        return enabled is bool value && value;
    }

    private bool BindMapVariants()
    {
        if (_mapVariantsBound)
        {
            return _mapVariantsProviders is not null && _installedVariant is not null;
        }

        lock (_mapVariantsLock)
        {
            if (_mapVariantsBound)
            {
                return _mapVariantsProviders is not null && _installedVariant is not null;
            }

            var providersType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("MapVariants.Server.Variants.Providers", throwOnError: false))
                .FirstOrDefault(type => type is not null);
            if (providersType is null)
            {
                _mapVariantsBound = true;
                return false;
            }

            _mapVariantsProviders = serviceProvider.GetService(providersType);
            _installedVariant = providersType.GetMethod(
                "InstalledVariant",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            _mapVariantsBound = true;
            return _mapVariantsProviders is not null && _installedVariant is not null;
        }
    }

    private void ReplaceGas(BotBase? bot, MongoId sessionId, BotGenerationDetails? details)
    {
        if (bot is null
            || details is null
            || !IsInfectedLabs(sessionId, details.Location)
            || !IsScourgeRole(bot.Info?.Settings?.Role))
        {
            return;
        }

        if (!templateTable.Items.ContainsKey(VaccineGasGrenade))
        {
            if (Interlocked.Exchange(ref _warnedMissingVaccine, 1) == 0)
            {
                logger.Warning("[Scourge] infected Labs vaccine grenade template is unavailable; retaining the squad's normal gas grenades.");
            }

            return;
        }

        foreach (var item in bot.Inventory?.Items ?? [])
        {
            if (item.Template == MissingGasGrenade || item.Template == CsGasGrenade)
            {
                item.Template = VaccineGasGrenade;
            }
        }
    }

    private static bool IsScourgeRole(string? role)
    {
        return string.Equals(role, ScourgeRoles.Boss, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, ScourgeRoles.Follower, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LocationSnapshotPatch : AbstractPatch
    {
        private static ScourgeInfectedLabsGas? _owner;

        public LocationSnapshotPatch(ScourgeInfectedLabsGas owner)
            : base($"{ModIdentity.Guid}.infected-labs-gas-location")
        {
            _owner = owner;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(LocationLifecycleService).GetMethod(
                       nameof(LocationLifecycleService.GenerateLocationAndLoot),
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       types: [typeof(MongoId), typeof(string), typeof(bool)],
                       modifiers: null)
                   ?? throw new MissingMethodException("[Scourge] LocationLifecycleService.GenerateLocationAndLoot is unavailable.");
        }

        [PatchPrefix]
        private static void Prefix(MongoId sessionId, string name) => _owner?.RememberLocation(sessionId, name);
    }

    private sealed class BotLoadoutPatch : AbstractPatch
    {
        private static ScourgeInfectedLabsGas? _owner;

        public BotLoadoutPatch(ScourgeInfectedLabsGas owner)
            : base($"{ModIdentity.Guid}.infected-labs-gas-bots")
        {
            _owner = owner;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(BotGenerator).GetMethod(
                       nameof(BotGenerator.PrepareAndGenerateBot),
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       types: [typeof(MongoId), typeof(BotGenerationDetails)],
                       modifiers: null)
                   ?? throw new MissingMethodException("[Scourge] BotGenerator.PrepareAndGenerateBot is unavailable.");
        }

        [PatchPostfix]
        private static void Postfix(
            MongoId sessionId,
            BotGenerationDetails botGenerationDetails,
            BotBase __result) => _owner?.ReplaceGas(__result, sessionId, botGenerationDetails);
    }
}
