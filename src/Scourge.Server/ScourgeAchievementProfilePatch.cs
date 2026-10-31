using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;

namespace Manimal.Scourge.Server;

/// <summary>
/// Backfills Scourging for profiles that completed Side Hustle before the
/// achievement was registered. The patch runs immediately before SPT clones
/// the saved profile for the complete-profile response.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class ScourgeAchievementProfilePatch : AbstractPatch
{
    private static readonly ConcurrentDictionary<MongoId, object> ProfileLocks = new();
    private static ScourgeAchievementProfilePatch? _instance;

    private readonly SaveServer _saveServer;
    private readonly RewardHelper _rewardHelper;
    private bool _enabled;

    public ScourgeAchievementProfilePatch(SaveServer saveServer, RewardHelper rewardHelper)
        : base($"{ModIdentity.Guid}.achievement-profile")
    {
        _saveServer = saveServer;
        _rewardHelper = rewardHelper;
        _instance = this;
    }

    internal void EnableAfterRegistration()
    {
        if (_enabled)
        {
            return;
        }

        Enable();
        _enabled = true;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileController).GetMethod(
                   nameof(ProfileController.GetCompleteProfile),
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   types: [typeof(MongoId)],
                   modifiers: null)
               ?? throw new MissingMethodException(
                   "[Scourge] SPT ProfileController.GetCompleteProfile(MongoId) is unavailable.");
    }

    [PatchPrefix]
    private static void Prefix(MongoId sessionId)
    {
        _instance?.SynchronizeProfile(sessionId);
    }

    private void SynchronizeProfile(MongoId sessionId)
    {
        var profile = _saveServer.GetProfile(sessionId);
        var pmcData = profile.CharacterData?.PmcData;
        if (pmcData?.Quests is null)
        {
            return;
        }

        var sideHustle = pmcData.Quests.FirstOrDefault(
            quest => quest.QId == new MongoId(ScourgeAchievementRegistration.SideHustleQuestIdText));
        if (sideHustle?.Status != QuestStatusEnum.Success)
        {
            return;
        }

        var achievementId = ScourgeAchievementRegistration.AchievementId;
        if (pmcData.Achievements?.ContainsKey(achievementId) == true)
        {
            return;
        }

        var profileLock = ProfileLocks.GetOrAdd(sessionId, static _ => new object());
        lock (profileLock)
        {
            // Re-check under the per-profile lock so concurrent profile
            // hydration requests cannot issue a second timestamp or reward.
            if (pmcData.Achievements?.ContainsKey(achievementId) == true)
            {
                return;
            }

            pmcData.Achievements ??= [];
            _rewardHelper.AddAchievementToProfile(profile, achievementId);
        }
    }
}
