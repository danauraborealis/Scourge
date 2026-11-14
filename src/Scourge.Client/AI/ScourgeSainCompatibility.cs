using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using EFT;
using Manimal.Scourge;
using SPT.Reflection.Patching;
using UnityEngine;
using ZLinq;

namespace Manimal.Scourge.Client.AI;

/// <summary>
/// Keeps the custom Scourge controller as a fallback while allowing SAIN to
/// own combat when its bot component is active. The compatibility layer only
/// changes the one squad relationship that Scourge needs: followers regroup
/// toward their living Scourge boss through SAIN's own mover and steering.
/// </summary>
internal static class ScourgeSainCompatibility
{
    private const string SainBotSpawnControllerTypeName =
        "SAIN.Components.BotController.BotSpawnController";
    private const string SainEnableClassTypeName = "SAIN.SAINEnableClass";
    private const string SainDecisionManagerTypeName =
        "SAIN.SAINComponent.Classes.Decision.BotDecisionManager";
    private const string SainSquadContainerTypeName =
        "SAIN.SAINComponent.Classes.Info.BotSquadContainer";

    private static readonly object Sync = new object();
    private static readonly List<ModulePatch> Patches = new List<ModulePatch>();
    private static readonly Dictionary<Type, PropertyInfo> OwnerProperties
        = new Dictionary<Type, PropertyInfo>();
    private static readonly Dictionary<Type, PropertyInfo> BotProperties
        = new Dictionary<Type, PropertyInfo>();
    private static readonly ConditionalWeakTable<BotOwner, RegroupState> RegroupStates
        = new ConditionalWeakTable<BotOwner, RegroupState>();

    private const float RegroupProgressSampleSeconds = 1f;
    private const float RegroupMinimumProgress = 0.5f;
    private const int RegroupStallSamplesBeforeRetry = 2;
    private const float RegroupRetryCooldownSeconds = 2f;
    private const float RegroupRetryBackoffSeconds = 4f;
    private const int RegroupRetryLimit = 3;

    private static bool _initialized;
    private static bool _sainInstalled;
    private static bool _ownershipResolved;
    private static bool _sainBridgeReady;
    private static Assembly _sainAssembly;
    private static PropertyInfo _botSpawnControllerInstance;
    private static MethodInfo _getSainMethod;
    private static MethodInfo _isSainDisabledForBotMethod;
    private static MethodInfo _setDecisionsMethod;
    private static MethodInfo _leaderComponentGetter;
    private static Type _squadDecisionType;
    private static int _combatNone;
    private static int _combatSearch;
    private static int _combatRushEnemy;
    private static int _combatMoveToEngage;
    private static int _combatCreepOnEnemy;
    private static int _combatStandAndShoot;
    private static int _combatShootDistantEnemy;
    private static int _combatRetreat;
    private static int _combatRunAway;
    private static int _combatDogFight;
    private static int _combatAvoidGrenade;
    private static int _combatMeleeAttack;
    private static int _selfNone;
    private static int _squadNone;
    private static int _squadRegroup;
    private static int[] _knownSquadDecisions;
    private static int _squadRetreat;
    private static int _squadBoundingRetreat;

    internal static bool SainAvailable => _sainInstalled;

    internal static bool ProtectionBridgeReady => ProtectionBridgeAvailable;

    private static bool ProtectionBridgeAvailable => _sainInstalled && _sainBridgeReady;

    private static void LogDebug(string message)
    {
        if (ScourgeClientPlugin.LogSource != null)
        {
            ScourgeClientPlugin.LogSource.LogDebug(message);
        }
    }

    private sealed class RegroupState
    {
        internal bool Active;
        internal string AnchorId;
        internal bool CloseGuard;
        // Kept as simple fields for the runtime probe and for cheap state
        // inspection; Progress mirrors these values for the pure policy API.
        internal float LastSampleTime = float.NegativeInfinity;
        internal float LastDistance;
        internal bool HasSample;
        internal int StallSamples;
        internal ScourgeRegroupProgressState Progress
            = ScourgeRegroupProgressState.Empty;
        internal int RetryCount;
        internal float RetryBlockedUntil = float.NegativeInfinity;

        internal void Reset(string anchorId, bool closeGuard)
        {
            Active = false;
            AnchorId = anchorId;
            CloseGuard = closeGuard;
            LastSampleTime = float.NegativeInfinity;
            LastDistance = 0f;
            HasSample = false;
            StallSamples = 0;
            Progress = ScourgeRegroupProgressState.Empty;
            RetryCount = 0;
            RetryBlockedUntil = float.NegativeInfinity;
        }

        internal void Stop()
        {
            Active = false;
            LastSampleTime = float.NegativeInfinity;
            LastDistance = 0f;
            HasSample = false;
            StallSamples = 0;
            Progress = ScourgeRegroupProgressState.Empty;
            RetryCount = 0;
            RetryBlockedUntil = float.NegativeInfinity;
        }
    }

    internal static bool Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return true;
            }

            _initialized = true;
            if (!Chainloader.PluginInfos.ContainsKey(ModIdentity.SainGuid))
            {
                ScourgeClientPlugin.LogSource.LogInfo(
                    "[Scourge] SAIN is not installed; using the custom squad controller.");
                return true;
            }

            try
            {
                ResolveSainTargets();
                _sainInstalled = true;
                _ownershipResolved = true;
                Patches.Add(new SainDecisionPatch(_setDecisionsMethod));
                Patches.Add(new SainLeaderPatch(_leaderComponentGetter));
                for (var index = 0; index < Patches.Count; index++)
                {
                    Patches[index].Enable();
                }

                _sainBridgeReady = true;
                ScourgeClientPlugin.LogSource.LogInfo(
                    "[Scourge] SAIN integration enabled: SAIN owns combat and Scourge guards use SAIN regroup behavior.");
                return true;
            }
            catch (Exception exception)
            {
                // If target resolution failed, the client must yield its custom
                // layers to SAIN because ownership is unknown. If only the
                // optional protection bridge failed, ownership still resolves
                // and custom combat remains suppressed for SAIN bots.
                _sainInstalled = true;
                _sainBridgeReady = false;
                ScourgeClientPlugin.LogSource.LogError(
                    $"[Scourge] SAIN integration could not initialize its protection bridge; SAIN ownership is retained where resolvable: {exception}");
                return true;
            }
        }
    }

    /// <summary>
    /// The custom layers are active only when this bot is not currently owned
    /// by SAIN. SAIN can be disabled for an individual role in its preset, so
    /// this is deliberately per bot rather than a global plugin check.
    /// </summary>
    internal static bool UseCustomController(BotOwner bot)
    {
        if (!SainAvailable || ReferenceEquals(bot, null))
        {
            return true;
        }

        if (!_ownershipResolved)
        {
            // SAIN is installed but its ownership API changed. Do not run a
            // second movement/aiming controller on top of an unknown SAIN bot.
            return false;
        }

        try
        {
            if (_isSainDisabledForBotMethod != null
                && (bool)_isSainDisabledForBotMethod.Invoke(null, new object[] { bot }))
            {
                return true;
            }

            var component = GetSainComponent(bot);
            if (component == null)
            {
                return true;
            }

            // SAINLayersActive is a scheduler state and is false while a bot
            // is idle. The component's existence is the ownership signal.
            return false;
        }
        catch (Exception exception)
        {
            LogDebug(
                $"[Scourge] SAIN ownership check failed for {bot.ProfileId}; yielding custom controller: {exception.Message}");
            return false;
        }
    }

    internal static bool TryGetProtectionBoss(BotOwner bot, out BotOwner boss)
    {
        boss = null;
        if (!ProtectionBridgeAvailable || ReferenceEquals(bot, null) || !ScourgeRoles.IsScourge(GetRole(bot)))
        {
            ResetRegroupState(bot);
            return false;
        }

        try
        {
            var context = ScourgeBotContext.Get(bot);
            context.RefreshProtectionOnly();
            if (context.ProtectionAnchorTerminal
                || !context.ProtectionActive
                || context.IsProtectionAnchor)
            {
                ResetRegroupState(bot);
                return false;
            }

            boss = context.ProtectionBoss;
            if (ReferenceEquals(boss, null)
                || boss.IsDead
                || boss.Profile?.Info?.Settings == null
                || (int)boss.Profile.Info.Settings.Role != ScourgeRoles.BossValue)
            {
                ResetRegroupState(bot);
                return false;
            }

            // Protection is injected only when both sides are actively owned
            // by SAIN. A disabled boss must not become a native SAIN leader or
            // regroup target for a still-enabled follower.
            if (!IsSainComponentActive(boss))
            {
                ResetRegroupState(bot);
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            LogDebug(
                $"[Scourge] protection anchor lookup failed for {bot.ProfileId}: {exception.Message}");
            boss = null;
            ResetRegroupState(bot);
            return false;
        }
    }

    internal static bool ShouldRegroup(BotOwner bot, bool hasEnemy)
    {
        if (!IsSainComponentActive(bot))
        {
            ResetRegroupState(bot);
            return false;
        }

        if (!TryGetProtectionBoss(bot, out var boss))
        {
            return false;
        }

        var context = ScourgeBotContext.Get(bot);
        if (context.NativeEmergency || !IsSainComponentActive(boss))
        {
            ResetRegroupState(bot);
            return false;
        }

        var state = RegroupStates.GetValue(bot, _ => new RegroupState());
        var closeGuard = context.IsCloseProtectionGuard;
        var anchorId = boss.ProfileId;
        if (!string.Equals(state.AnchorId, anchorId, StringComparison.Ordinal)
            || state.CloseGuard != closeGuard)
        {
            state.Reset(anchorId, closeGuard);
        }

        var distance = context.ProtectionDistance;
        var request = ScourgeProtectionPolicy.ShouldRegroup(
            bossAlive: !ReferenceEquals(boss, null) && !boss.IsDead,
            isAnchor: false,
            isCloseGuard: closeGuard,
            // SetDecisions supplies the authoritative current enemy. The
            // protection-only context intentionally does not refresh custom
            // combat memory, so never let a stale HasContact suppress a
            // no-enemy regroup request.
            hasContact: hasEnemy,
            protectionRevisionChanged: context.ProtectionRevisionChanged,
            distanceToBoss: distance,
            settings: context.ProtectionSettings);

        var now = Time.time;
        var justActivated = false;
        var retryCooldownExpired = false;
        if (!state.Active)
        {
            if (!request || now < state.RetryBlockedUntil)
            {
                if (now < state.RetryBlockedUntil)
                {
                    ResetRegroupSample(state);
                }
                return false;
            }

            state.Active = true;
            state.RetryCount = 0;
            ResetRegroupSample(state);
            state.RetryBlockedUntil = float.NegativeInfinity;
            justActivated = true;
        }
        else if (!ScourgeProtectionPolicy.ShouldMaintainRegroup(
                     regroupActive: true,
                     bossAlive: !ReferenceEquals(boss, null) && !boss.IsDead,
                     isAnchor: false,
                     isCloseGuard: closeGuard,
                     distanceToBoss: distance,
                     settings: context.ProtectionSettings))
        {
            state.Stop();
            return false;
        }

        if (now < state.RetryBlockedUntil)
        {
            // Do not count stationary samples while SAIN is in the bounded
            // retry cooldown. A retry must measure a new movement window.
            ResetRegroupSample(state);
            return false;
        }

        if (state.RetryBlockedUntil > float.NegativeInfinity)
        {
            state.RetryBlockedUntil = float.NegativeInfinity;
            retryCooldownExpired = true;
        }

        if (justActivated)
        {
            SeedRegroupSample(state, distance, now);
            return true;
        }

        if (retryCooldownExpired)
        {
            // The first decision after cooldown is an actual retry. Seed its
            // sample now so later stall checks only measure this retry route.
            SeedRegroupSample(state, distance, now);
            return true;
        }

        if (ObserveRegroupProgress(state, distance, now))
        {
            state.RetryCount++;
            ResetRegroupSample(state);
            state.RetryBlockedUntil = now + (state.RetryCount >= RegroupRetryLimit
                ? RegroupRetryBackoffSeconds
                : RegroupRetryCooldownSeconds);
            // Keep the active state so hysteresis still applies after the
            // bounded retry cooldown; SAIN remains the movement writer.
            return false;
        }

        return true;
    }

    internal static bool IsProtectionAnchorTerminal(BotOwner bot)
    {
        if (!ProtectionBridgeAvailable || ReferenceEquals(bot, null) || !IsScourge(bot))
        {
            return false;
        }

        try
        {
            var context = ScourgeBotContext.Get(bot);
            context.RefreshProtectionOnly();
            return context.ProtectionAnchorTerminal;
        }
        catch (Exception exception)
        {
            LogDebug(
                $"[Scourge] protection anchor state lookup failed for {bot.ProfileId}: {exception.Message}");
            return false;
        }
    }

    private static void ResetRegroupState(BotOwner bot)
    {
        if (!ReferenceEquals(bot, null) && RegroupStates.TryGetValue(bot, out var state))
        {
            state.Stop();
            state.AnchorId = null;
        }
    }

    private static bool ObserveRegroupProgress(RegroupState state, float distance, float now)
    {
        var current = new ScourgeRegroupProgressState(
            state.HasSample,
            state.LastDistance,
            state.LastSampleTime,
            state.StallSamples);
        var stalled = ScourgeProtectionPolicy.ObserveRegroupProgress(
            current,
            distance,
            now,
            out var next,
            RegroupProgressSampleSeconds,
            RegroupMinimumProgress,
            RegroupStallSamplesBeforeRetry);
        state.Progress = next;
        state.HasSample = next.HasSample;
        state.LastDistance = next.LastDistance;
        state.LastSampleTime = next.LastSampleTime;
        state.StallSamples = next.StallSamples;
        if (!stalled)
        {
            // A measurable inward step clears consecutive stall retries.
            if (state.Progress.StallSamples == 0)
            {
                state.RetryCount = 0;
            }
        }

        return stalled;
    }

    private static void ResetRegroupSample(RegroupState state)
    {
        state.Progress = ScourgeRegroupProgressState.Empty;
        state.HasSample = false;
        state.LastDistance = 0f;
        state.LastSampleTime = float.NegativeInfinity;
        state.StallSamples = 0;
    }

    private static void SeedRegroupSample(RegroupState state, float distance, float now)
    {
        state.Progress = ScourgeRegroupProgressState.Empty.Seed(distance, now);
        state.HasSample = true;
        state.LastDistance = distance;
        state.LastSampleTime = now;
        state.StallSamples = 0;
    }

    private static void ResolveSainTargets()
    {
        _sainAssembly = Chainloader.PluginInfos[ModIdentity.SainGuid].Instance?.GetType().Assembly
            ?? FindSainAssembly();
        if (_sainAssembly == null)
        {
            throw new InvalidOperationException("SAIN assembly was not found.");
        }

        var controllerType = RequireType(SainBotSpawnControllerTypeName);
        _botSpawnControllerInstance = RequireProperty(
            controllerType,
            "Instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _getSainMethod = controllerType.GetMethod(
            "GetSAIN",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(BotOwner) },
            null);
        RequireMethod(_getSainMethod, "GetSAIN(BotOwner)");

        var enableType = RequireType(SainEnableClassTypeName);
        _isSainDisabledForBotMethod = enableType.GetMethod(
            "IsSAINDisabledForBot",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(BotOwner) },
            null);
        RequireMethod(_isSainDisabledForBotMethod, "IsSAINDisabledForBot(BotOwner)");

        var decisionType = RequireType(SainDecisionManagerTypeName);
        _setDecisionsMethod = decisionType.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Instance)
            .AsValueEnumerable()
            .FirstOrDefault(method =>
                string.Equals(method.Name, "SetDecisions", StringComparison.Ordinal)
                && method.ReturnType == typeof(void)
                && method.GetParameters().Length == 4);
        if (_setDecisionsMethod == null
            || _setDecisionsMethod.ReturnType != typeof(void)
            || _setDecisionsMethod.GetParameters().Length != 4)
        {
            throw new MissingMethodException(decisionType.FullName, "SetDecisions");
        }

        _squadDecisionType = _setDecisionsMethod.GetParameters()[1].ParameterType;
        if (!string.Equals(_squadDecisionType.Name, "ESquadDecision", StringComparison.Ordinal))
        {
            throw new MissingMemberException(_squadDecisionType.FullName, "ESquadDecision");
        }

        _combatNone = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "None");
        _combatSearch = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "Search");
        _combatRushEnemy = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "RushEnemy");
        _combatMoveToEngage = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "MoveToEngage");
        _combatCreepOnEnemy = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "CreepOnEnemy");
        _combatStandAndShoot = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "StandAndShoot");
        _combatShootDistantEnemy = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "ShootDistantEnemy");
        _combatRetreat = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "Retreat");
        _combatRunAway = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "RunAway");
        _combatDogFight = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "DogFight");
        _combatAvoidGrenade = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "AvoidGrenade");
        _combatMeleeAttack = EnumValue(_setDecisionsMethod.GetParameters()[0].ParameterType, "MeleeAttack");
        _selfNone = EnumValue(_setDecisionsMethod.GetParameters()[2].ParameterType, "None");
        _squadNone = EnumValue(_squadDecisionType, "None");
        _squadRegroup = EnumValue(_squadDecisionType, "Regroup");
        _knownSquadDecisions = new[]
        {
            _squadNone,
            _squadRegroup,
            EnumValue(_squadDecisionType, "Surround"),
            EnumValue(_squadDecisionType, "Suppress"),
            EnumValue(_squadDecisionType, "PushSuppressedEnemy"),
            EnumValue(_squadDecisionType, "SpreadOut"),
            EnumValue(_squadDecisionType, "HoldPositions"),
            EnumValue(_squadDecisionType, "Help"),
            EnumValue(_squadDecisionType, "Search"),
            EnumValue(_squadDecisionType, "GroupSearch"),
            EnumValue(_squadDecisionType, "Retreat"),
            EnumValue(_squadDecisionType, "BoundingRetreat")
        };
        _squadRetreat = EnumValue(_squadDecisionType, "Retreat");
        _squadBoundingRetreat = EnumValue(_squadDecisionType, "BoundingRetreat");

        var squadType = RequireType(SainSquadContainerTypeName);
        _leaderComponentGetter = squadType.GetProperty(
            "LeaderComponent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
        RequireMethod(_leaderComponentGetter, "get_LeaderComponent()");
    }

    private static Assembly FindSainAssembly()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .AsValueEnumerable()
            .FirstOrDefault(assembly =>
                assembly.GetType(SainBotSpawnControllerTypeName, false) != null);
    }

    private static Type RequireType(string fullName)
    {
        return _sainAssembly.GetType(fullName, true);
    }

    private static PropertyInfo RequireProperty(
        Type type,
        string name,
        BindingFlags flags)
    {
        var property = type.GetProperty(name, flags);
        if (property == null)
        {
            throw new MissingMemberException(type.FullName, name);
        }

        return property;
    }

    private static PropertyInfo FindProperty(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    private static void RequireMethod(MethodInfo method, string name)
    {
        if (method == null)
        {
            throw new MissingMethodException(name);
        }
    }

    private static int EnumValue(Type enumType, string name)
    {
        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"SAIN decision type '{enumType.FullName}' is not an enum.");
        }

        return Convert.ToInt32(Enum.Parse(enumType, name, ignoreCase: false));
    }

    private static object GetSainComponent(BotOwner bot)
    {
        var controller = _botSpawnControllerInstance?.GetValue(null, null);
        return controller == null
            ? null
            : _getSainMethod.Invoke(controller, new object[] { bot });
    }

    private static bool IsSainComponentActive(BotOwner bot)
    {
        if (!_sainInstalled || !_ownershipResolved || ReferenceEquals(bot, null))
        {
            return false;
        }

        try
        {
            if (_isSainDisabledForBotMethod != null
                && (bool)_isSainDisabledForBotMethod.Invoke(null, new object[] { bot }))
            {
                return false;
            }

            return GetSainComponent(bot) != null;
        }
        catch (Exception exception)
        {
            // Bridge methods are optional. If a signature starts throwing,
            // fail closed for protection injection rather than target a stale
            // or unrelated SAIN component.
            LogDebug(
                $"[Scourge] SAIN active-component check failed for {bot.ProfileId}: {exception.Message}");
            return false;
        }
    }

    private static BotOwner GetBotOwner(object value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is BotOwner bot)
        {
            return bot;
        }

        var type = value.GetType();
        if (!OwnerProperties.TryGetValue(type, out var botOwnerProperty))
        {
            botOwnerProperty = FindProperty(type, "BotOwner");
            if (botOwnerProperty != null)
            {
                OwnerProperties[type] = botOwnerProperty;
            }
        }
        if (botOwnerProperty?.GetValue(value, null) is BotOwner direct)
        {
            return direct;
        }

        if (!BotProperties.TryGetValue(type, out var botProperty))
        {
            botProperty = FindProperty(type, "Bot");
            if (botProperty != null)
            {
                BotProperties[type] = botProperty;
            }
        }
        if (botProperty == null)
        {
            return null;
        }

        var nested = botProperty.GetValue(value, null);
        return ReferenceEquals(nested, value) ? null : GetBotOwner(nested);
    }

    private static int GetRole(BotOwner bot)
    {
        return bot?.Profile?.Info?.Settings == null
            ? -1
            : (int)bot.Profile.Info.Settings.Role;
    }

    private static bool IsScourge(BotOwner bot)
    {
        return ScourgeRoles.IsScourge(GetRole(bot));
    }

    private static object GetBossSainComponent(BotOwner boss)
    {
        return GetSainComponent(boss);
    }

    private sealed class SainDecisionPatch : ModulePatch
    {
        private readonly MethodBase _targetMethod;

        internal SainDecisionPatch(MethodBase targetMethod)
        {
            _targetMethod = targetMethod;
        }

        protected override MethodBase GetTargetMethod() => _targetMethod;

        [PatchPrefix]
        private static void Prefix(object __instance, object[] __args)
        {
            try
            {
                var bot = GetBotOwner(__instance);
                if (__args == null || __args.Length < 4 || !IsScourge(bot)
                    || !IsSainComponentActive(bot))
                {
                    return;
                }

                var combat = Convert.ToInt32(__args[0]);
                var selfAction = Convert.ToInt32(__args[2]);
                var squad = Convert.ToInt32(__args[1]);

                // A terminal anchor must never allow SAIN's leader election to
                // keep a pending Regroup route aimed at a replacement guard.
                // Leave the original combat/self decisions unchanged.
                if (IsProtectionAnchorTerminal(bot))
                {
                    if (squad == _squadRegroup)
                    {
                        __args[1] = Enum.ToObject(_squadDecisionType, _squadNone);
                    }

                    ResetRegroupState(bot);
                    return;
                }

                // Preserve SAIN's self action, emergency/recovery decisions,
                // retreat decisions, and unknown values. Only ordinary combat
                // decisions may yield to the protection route.
                if (combat == _combatRetreat
                    || combat == _combatRunAway
                    || combat == _combatDogFight
                    || combat == _combatAvoidGrenade
                    || combat == _combatMeleeAttack)
                {
                    return;
                }

                if (!ScourgeProtectionPolicy.ShouldOverlayRegroup(
                        combat,
                        squad,
                        selfAction,
                        _selfNone,
                        _squadRetreat,
                        _squadBoundingRetreat,
                        _combatNone,
                        _combatSearch,
                        _combatRushEnemy,
                        _combatMoveToEngage,
                        _combatCreepOnEnemy,
                        _combatStandAndShoot,
                        _combatShootDistantEnemy,
                        _knownSquadDecisions)
                )
                {
                    return;
                }

                var regroupWasActive = RegroupStates.TryGetValue(bot, out var regroupState)
                    && regroupState.Active;
                if (!ShouldRegroup(bot, __args[3] != null))
                {
                    // If this bridge previously requested the route, release
                    // an already-selected native Regroup when hysteresis says
                    // the follower is settled or a bounded stall cooldown is
                    // in effect. Combat and self-action arguments remain
                    // untouched. A SAIN-native Regroup that Scourge did not
                    // request is left alone.
                    if (squad == _squadRegroup
                        && regroupWasActive
                        && CanCancelPendingRegroup(bot))
                    {
                        __args[1] = Enum.ToObject(_squadDecisionType, _squadNone);
                    }

                    return;
                }

                __args[1] = Enum.ToObject(_squadDecisionType, _squadRegroup);
            }
            catch (Exception exception)
            {
                LogDebug(
                    $"[Scourge] SAIN regroup bridge yielded: {exception.Message}");
            }
        }
    }

    private static bool CanCancelPendingRegroup(BotOwner bot)
    {
        if (!TryGetProtectionBoss(bot, out _))
        {
            return false;
        }

        var context = ScourgeBotContext.Get(bot);
        return !context.NativeEmergency
            && context.ProtectionAnchorLocked
            && !context.ProtectionAnchorTerminal;
    }

    private sealed class SainLeaderPatch : ModulePatch
    {
        private readonly MethodBase _targetMethod;

        internal SainLeaderPatch(MethodBase targetMethod)
        {
            _targetMethod = targetMethod;
        }

        protected override MethodBase GetTargetMethod() => _targetMethod;

        [PatchPostfix]
        private static void Postfix(object __instance, ref object __result)
        {
            try
            {
                var follower = GetBotOwner(__instance);
                if (!IsScourge(follower)
                    || !IsSainComponentActive(follower)
                    || !TryGetProtectionBoss(follower, out var boss)
                    || !ScourgeRoles.IsScourge(GetRole(boss)))
                {
                    return;
                }

                var bossComponent = GetBossSainComponent(boss);
                if (bossComponent != null)
                {
                    __result = bossComponent;
                }
            }
            catch (Exception exception)
            {
                LogDebug(
                    $"[Scourge] SAIN leader bridge yielded: {exception.Message}");
            }
        }
    }
}
