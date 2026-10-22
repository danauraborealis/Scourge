using MoreBotsServer.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Bot.GlobalSettings;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Scourge.Server;

// Finish after MoreBots and other preload mods have registered their custom roles.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.GameCallbacks - 1)]
public sealed class ScourgeHostility(
    BotTable botTable,
    MoreBotsCustomBotTypeService customTypes,
    ISptLogger<ScourgeHostility> logger) : IOnLoad
{
    private static readonly HashSet<string> ScourgeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ScourgeRoles.Boss,
        ScourgeRoles.Follower,
    };

    private static readonly WildSpawnType[] OwnRoles =
    [
        (WildSpawnType)ScourgeRoles.BossValue,
        (WildSpawnType)ScourgeRoles.FollowerValue,
    ];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var knownRoles = Enum.GetValues<WildSpawnType>()
            .Concat(customTypes.CustomWildSpawnTypes.Keys.Select(id => (WildSpawnType)id));
        var count = Normalize(botTable.Types, knownRoles);
        logger.Info($"[Scourge] Applied exclusive Scourge allegiance to {count} difficulty profiles.");
        return Task.CompletedTask;
    }

    public static int Normalize(Dictionary<string, BotType?> types, IEnumerable<WildSpawnType> knownRoles)
    {
        var ownRoles = OwnRoles.ToHashSet();
        var enemies = knownRoles
            .Where(role => !ownRoles.Contains(role))
            .Distinct()
            .OrderBy(role => (int)role)
            .ToList();
        var count = 0;

        foreach (var (name, bot) in types)
        {
            if (bot?.BotDifficulty is null)
            {
                continue;
            }

            var isScourge = ScourgeNames.Contains(name);
            foreach (var difficulty in bot.BotDifficulty.Values)
            {
                if (difficulty?.Mind is null)
                {
                    continue;
                }

                var mind = difficulty.Mind;
                if (isScourge)
                {
                    mind.EnemyBotTypes = new(enemies);
                    mind.FriendlyBotTypes = new(OwnRoles);
                    mind.WarnBotTypes = [];
                    mind.RevengeBotTypes = new(OwnRoles);

                    // Keep player and player-scav behavior hostile even when the
                    // imported profile came from a donor with different defaults.
                    mind.DefaultBearBehaviour = BotGlobalsMindSettings.EWarnBehaviour.AlwaysEnemies;
                    mind.DefaultUsecBehaviour = BotGlobalsMindSettings.EWarnBehaviour.AlwaysEnemies;
                    mind.DefaultSavageBehaviour = BotGlobalsMindSettings.EWarnBehaviour.AlwaysEnemies;
                    mind.BossIgnoreLoyalty = true;
                    mind.UseAddToEnemyValidation = false;
                    mind.CheckMarkOfUnknows = false;
                    mind.ReactAddDrunkEnemy = false;
                    if (difficulty.Boss is not null)
                    {
                        difficulty.Boss.ShallWarn = false;
                    }
                }
                else
                {
                    // Preserve other factions' relations except their stance toward Scourge.
                    mind.EnemyBotTypes = (mind.EnemyBotTypes ?? [])
                        .Concat(OwnRoles)
                        .Distinct()
                        .ToList();
                    mind.FriendlyBotTypes?.RemoveAll(ownRoles.Contains);
                    mind.WarnBotTypes?.RemoveAll(ownRoles.Contains);
                    mind.RevengeBotTypes?.RemoveAll(ownRoles.Contains);
                }

                count++;
            }
        }

        return count;
    }
}
