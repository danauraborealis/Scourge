using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace Manimal.Scourge.Client;

internal static class ScourgeHostilityPatches
{
    internal static void Enable()
    {
        new ScourgeEnemyDecisionPatch().Enable();
        new ScourgeCheckEnemyPatch().Enable();
        new ScourgeAddEnemyPatch().Enable();
    }

    private static bool TryGetEnemyDecision(BotsGroup group, IPlayer player, out bool isEnemy)
    {
        isEnemy = false;
        if (group == null || player == null) return false;

        var role = 0;
        if (player.IsAI)
        {
            var settings = player.AIData?.BotOwner?.Profile?.Info?.Settings;
            if (settings == null) return false;
            role = (int)settings.Role;
        }

        return ScourgeHostilityPolicy.TryGetEnemyDecision(
            (int)group.InitialBotType, player.IsAI, role, out isEnemy);
    }

    private sealed class ScourgeEnemyDecisionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BotsGroup).GetMethod(nameof(BotsGroup.IsPlayerEnemy), new[] { typeof(IPlayer) });

        [PatchPostfix]
        private static void Postfix(BotsGroup __instance, IPlayer player, ref bool __result)
        {
            // Apply after native map/faction/loyalty decisions, including
            // player-scav and Mark of the Unheard friendship exceptions.
            if (TryGetEnemyDecision(__instance, player, out var isEnemy))
            {
                __result = isEnemy;
            }
        }
    }

    private sealed class ScourgeCheckEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BotsGroup).GetMethod(
                nameof(BotsGroup.CheckAndAddEnemy),
                new[] { typeof(IPlayer), typeof(bool) });

        [PatchPrefix]
        private static bool Prefix(
            BotsGroup __instance,
            IPlayer player,
            bool ignoreAI,
            ref bool __result)
        {
            if (!TryGetEnemyDecision(__instance, player, out var isEnemy)) return true;

            // Native AlwaysFriends checks would bypass IsPlayerEnemy here.
            // Keep life/self/ignoreAI/duplicate guards and let AddEnemy own
            // initialization, memory updates, events and ally cleanup.
            __result = isEnemy && player.HealthController?.IsAlive == true
                && (!player.IsAI || (ignoreAI && __instance._initialBot != null
                    && __instance._initialBot.Id != player.AIData.BotOwner.Id))
                && !__instance.Enemies.ContainsKey(player)
                && __instance.AddEnemy(player, EBotEnemyCause.checkAddTODO);
            return false;
        }
    }

    private sealed class ScourgeAddEnemyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BotsGroup).GetMethod(
                nameof(BotsGroup.AddEnemy),
                new[] { typeof(IPlayer), typeof(EBotEnemyCause) });

        [PatchPrefix]
        private static bool Prefix(BotsGroup __instance, IPlayer person, ref bool __result)
        {
            // Damage and revenge can enter AddEnemy without either decision
            // method. Squadmates must remain allies on that path too.
            if (TryGetEnemyDecision(__instance, person, out var isEnemy) && !isEnemy)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
}
