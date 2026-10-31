using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using WttCommonFacade = WTTServerCommonLib.WTTServerCommonLib;
using Path = System.IO.Path;

namespace Manimal.Scourge.Server;

/// <summary>
/// Registers Scourging through WTT CommonLib, adds its native quest reward,
/// and enables the profile hydration compatibility patch only after all of
/// the registration checks have passed.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.GameCallbacks - 1)]
public sealed class ScourgeAchievementRegistration(
    WttCommonFacade commonLib,
    TemplateTable templateTable,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    ScourgeAchievementProfilePatch profilePatch,
    ISptLogger<ScourgeAchievementRegistration> logger) : IOnLoad
{
    internal const string AchievementIdText = "6ad61d4b93495b6d53062150";
    internal const string SideHustleQuestIdText = "6ac4c42976842fabb501d5b8";
    internal const string AchievementRewardIdText = "6ad61d4b93495b6d53062151";

    internal static readonly MongoId AchievementId = new(AchievementIdText);
    private static readonly MongoId SideHustleQuestId = new(SideHustleQuestIdText);
    private static readonly MongoId AchievementRewardId = new(AchievementRewardIdText);

    private const string AchievementRelativePath = "db/CustomAchievements/Achievements/scourging.json";
    private const string LocaleRelativePath = "db/CustomAchievements/Locales/en.json";
    private const string ImageRelativePath = "db/CustomAchievements/Images/scourging-6ad61d4b93495b6d53062150.png";

    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RegistrationGate.WaitAsync(cancellationToken);
        try
        {
            var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            PreflightSourceFiles(modPath);

            var existingCount = CountAchievement(AchievementId);
            if (existingCount > 1)
            {
                throw new InvalidOperationException(
                    $"[Scourge] achievement registration found {existingCount} existing copies of '{AchievementIdText}'; "
                    + "refusing to continue with an ambiguous achievement catalog.");
            }

            if (existingCount == 0)
            {
                await commonLib.CustomAchievementService
                    .CreateCustomAchievements(Assembly.GetExecutingAssembly());
            }
            else
            {
                // CommonLib appends loaded records. Preserve a compatible
                // achievement supplied by another mod or an earlier load,
                // rather than appending a duplicate record on re-entry.
                logger.Warning(
                    $"[Scourge] achievement '{AchievementIdText}' already exists; preserving the existing template.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var registeredCount = CountAchievement(AchievementId);
            if (registeredCount != 1)
            {
                throw new InvalidOperationException(
                    $"[Scourge] achievement registration expected exactly one '{AchievementIdText}' template after CommonLib load, "
                    + $"found {registeredCount}.");
            }

            AddNativeQuestReward();
            profilePatch.EnableAfterRegistration();
            logger.Info(
                $"[Scourge] registered achievement '{AchievementIdText}' and linked it to Side Hustle completion.");
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    private int CountAchievement(MongoId achievementId)
    {
        return templateTable.Achievements.Count(achievement => achievement.Id == achievementId);
    }

    private void PreflightSourceFiles(string modPath)
    {
        var achievementPath = Path.Combine(modPath, AchievementRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var localePath = Path.Combine(modPath, LocaleRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var imagePath = Path.Combine(modPath, ImageRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var missing = new List<string>();
        if (!File.Exists(achievementPath))
        {
            missing.Add(achievementPath);
        }

        if (!File.Exists(localePath))
        {
            missing.Add(localePath);
        }

        if (!File.Exists(imagePath))
        {
            missing.Add(imagePath);
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                "[Scourge] achievement registration preflight is missing required achievement, locale, or icon files: "
                + string.Join(", ", missing));
        }

        var achievementDefinitions = jsonUtil.DeserializeFromFile<List<Achievement>>(achievementPath)
            ?? throw new InvalidDataException($"[Scourge] achievement data is missing or invalid: {achievementPath}");
        if (achievementDefinitions.Count != 1 || achievementDefinitions[0].Id != AchievementId)
        {
            throw new InvalidDataException(
                $"[Scourge] {AchievementRelativePath} must contain exactly one achievement with id '{AchievementIdText}'.");
        }

        var locale = jsonUtil.DeserializeFromFile<Dictionary<string, string>>(localePath)
            ?? throw new InvalidDataException($"[Scourge] achievement locale is missing or invalid: {localePath}");
        if (!locale.TryGetValue($"{AchievementIdText} name", out var name)
            || !string.Equals(name, "Scourging", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"[Scourge] {LocaleRelativePath} must define '{AchievementIdText} name' as 'Scourging'.");
        }
    }

    private void AddNativeQuestReward()
    {
        if (!templateTable.Quests.TryGetValue(SideHustleQuestId, out var sideHustle) || sideHustle is null)
        {
            throw new InvalidOperationException(
                $"[Scourge] cannot link achievement '{AchievementIdText}': Side Hustle quest '{SideHustleQuestIdText}' is not registered.");
        }

        if (sideHustle.Rewards is null)
        {
            throw new InvalidDataException(
                $"[Scourge] Side Hustle quest '{SideHustleQuestIdText}' has no reward map; refusing to replace quest data.");
        }

        if (!sideHustle.Rewards.TryGetValue(QuestStatusEnum.Success.ToString(), out var successRewards)
            || successRewards is null)
        {
            throw new InvalidDataException(
                $"[Scourge] Side Hustle quest '{SideHustleQuestIdText}' has no Success reward list; refusing to replace quest data.");
        }

        var existingAchievementRewards = successRewards
            .Where(reward => reward.Type == RewardType.Achievement
                && string.Equals(reward.Target, AchievementIdText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (existingAchievementRewards.Count > 1)
        {
            throw new InvalidDataException(
                $"[Scourge] Side Hustle has {existingAchievementRewards.Count} duplicate Scourging achievement rewards; refusing to award it ambiguously.");
        }

        if (existingAchievementRewards.Count == 1)
        {
            return;
        }

        if (successRewards.Any(reward => reward.Id == AchievementRewardId))
        {
            throw new InvalidDataException(
                $"[Scourge] Side Hustle Success reward id '{AchievementRewardIdText}' is already used by another reward.");
        }

        successRewards.Add(new Reward
        {
            Id = AchievementRewardId,
            Type = RewardType.Achievement,
            Target = AchievementIdText,
        });
    }
}
