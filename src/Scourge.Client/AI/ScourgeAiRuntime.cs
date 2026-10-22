using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Manimal.Scourge;
using Manimal.Scourge.Client;
using ZLinq;

namespace Manimal.Scourge.Client.AI;

internal static class ScourgeAiRuntime
{
    private const string ArenaFighterBrain = "ArenaFighter";
    private const int CombatPriority = 71;
    private const int RecoveryPriority = 72;
    private const int EscortPriority = 73;

    internal static bool Ready { get; private set; }
    internal static string FailureReason { get; private set; } = "not initialized";

    internal static readonly List<WildSpawnType> Roles = new List<WildSpawnType>
    {
        (WildSpawnType)ScourgeRoles.BossValue,
        (WildSpawnType)ScourgeRoles.FollowerValue
    };

    internal static bool Initialize()
    {
        Ready = false;
        FailureReason = "initialization failed";

        if (!ScourgeAiConfig.Enabled)
        {
            FailureReason = "disabled by configuration";
            return false;
        }

        try
        {
            if (!ScourgeSainCompatibility.Initialize())
            {
                FailureReason = "SAIN compatibility could not resolve its required signatures";
                return false;
            }

            RegisterLayers();
            Ready = true;

            if (!ScourgeSainCompatibility.CompleteInitialization())
            {
                Ready = false;
                if (string.IsNullOrEmpty(FailureReason))
                {
                    FailureReason = "SAIN layer restoration failed";
                }
                ScourgeClientPlugin.LogSource.LogError(
                    "[Scourge] tactics disabled because SAIN layer restoration was not safe; existing AI configuration is retained.");
                return false;
            }

            FailureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Ready = false;
            FailureReason = exception.GetType().Name + ": " + exception.Message;
            ScourgeClientPlugin.LogSource.LogError(
                $"[Scourge] tactics could not initialize; existing AI configuration is retained: {exception}");
            return false;
        }
    }

    internal static void Disable(string reason)
    {
        Ready = false;
        FailureReason = string.IsNullOrEmpty(reason)
            ? "disabled by compatibility failure"
            : reason;
    }

    private static void RegisterLayers()
    {
        var selected = Roles.AsValueEnumerable()
            .Where(role => ScourgeRoles.IsScourge((int)role))
            .Distinct()
            .ToList();
        if (selected.Count != 2)
        {
            throw new InvalidOperationException(
                "The Scourge custom-role allowlist did not resolve exactly two roles.");
        }

        var brains = new List<string> { ArenaFighterBrain };
        BrainManager.AddCustomLayer(
            typeof(ScourgeCombatLayer),
            brains,
            CombatPriority,
            selected);
        BrainManager.AddCustomLayer(
            typeof(ScourgeRecoveryLayer),
            brains,
            RecoveryPriority,
            selected);

        var follower = selected.AsValueEnumerable()
            .Where(role => (int)role == ScourgeRoles.FollowerValue)
            .ToList();
        BrainManager.AddCustomLayer(
            typeof(ScourgeEscortLayer),
            brains,
            EscortPriority,
            follower);
    }
}
