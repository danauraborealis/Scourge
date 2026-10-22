using System;
using BepInEx;
using BepInEx.Logging;
using EFT;
using Manimal.Scourge.Client.AI;

namespace Manimal.Scourge.Client;

[BepInPlugin(ModIdentity.Guid, ModIdentity.ClientName, ModIdentity.Version)]
[BepInDependency(ModIdentity.MoreBotsGuid, ModIdentity.MoreBotsVersion)]
[BepInDependency(ModIdentity.WttGuid, ModIdentity.WttVersion)]
[BepInDependency(ModIdentity.BigBrainGuid, ModIdentity.BigBrainVersion)]
[BepInDependency(ModIdentity.UnityToolkitGuid)]
[BepInDependency(ModIdentity.SainGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class ScourgeClientPlugin : BaseUnityPlugin
{
    internal static ManualLogSource LogSource = null!;

    private void Awake()
    {
        LogSource = Logger;
        ScourgeAiConfig.Bind(Config);

        if (!TryGetPatchedRole(ScourgeRoles.Boss, ScourgeRoles.BossValue, out var boss)
            || !TryGetPatchedRole(ScourgeRoles.Follower, ScourgeRoles.FollowerValue, out var follower))
        {
            Logger.LogError("[Scourge] custom roles are unavailable. Ensure the MoreBots prepatcher ran before launching EFT.");
            return;
        }

        try
        {
            ScourgeHostilityPatches.Enable();
        }
        catch (Exception exception)
        {
            Logger.LogError($"[Scourge] hostility patches could not initialize; existing AI configuration is retained: {exception}");
            return;
        }

        Logger.LogInfo($"[Scourge] client roles ready: {boss}={(int)boss}, {follower}={(int)follower}; hostile to everyone except Scourge squadmates.");

        if (!ScourgeAiConfig.Enabled)
        {
            Logger.LogInfo("[Scourge] custom tactics are disabled by configuration; existing AI configuration is retained.");
            return;
        }

        if (!ScourgeAiRuntime.Initialize())
        {
            Logger.LogWarning($"[Scourge] custom tactics are unavailable ({ScourgeAiRuntime.FailureReason}); existing AI configuration is retained.");
            return;
        }

        Logger.LogInfo("[Scourge] Arena Fighter combat, recovery and follower escort layers are ready.");
    }

    private bool TryGetPatchedRole(string roleName, int expectedValue, out WildSpawnType role)
    {
        if (!Enum.TryParse(roleName, ignoreCase: false, out role)
            || (int)role != expectedValue
            || !string.Equals(Enum.GetName(typeof(WildSpawnType), role), roleName, StringComparison.Ordinal))
        {
            role = default;
            return false;
        }

        return true;
    }
}
