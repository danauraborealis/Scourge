using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Manimal.Scourge;
using Manimal.Scourge.Client;
using SPT.Reflection.Patching;
using ZLinq;

namespace Manimal.Scourge.Client.AI;

internal static class ScourgeSainCompatibility
{
    private const string SainBotSpawnControllerTypeName =
        "SAIN.Components.BotController.BotSpawnController";
    private const string SainBrainAssignmentTypeName =
        "SAIN.BigBrainHandler+BrainAssignment";
    private const string SainBigBrainHandlerTypeName = "SAIN.BigBrainHandler";
    private const string BigBrainHelpersTypeName = "DrakiaXYZ.BigBrain.Internal.BrainHelpers";
    private const string SainLayerNamesPropertyName = "SAINLayerNames";
    private const string CommonVanillaLayersFieldName = "_commonVanillaLayersToRemove";
    private const string CommonVanillaBossAndFollowerLayersFieldName =
        "_commonVanillaBossAndFollowerLayersToRemove";
    private const string ArenaFighterBrainName = "ArenaFighter";

    // ArenaFighterLayersStrategy in the installed client (SPT 4.1.6) supplies
    // these exact native layer names. The request layers include the exact
    // no-request forms below and have state-dependent names; ReadNativeLayerNames
    // adds those only when an existing exclusion contains a verified Name() form.
    private static readonly string[] ArenaFighterNativeLayerNames =
    {
        "AvoidDanger",
        "Malfunction",
        "FightReqNull",
        "Pmc",
        "AssaultHaveEnemy",
        "PeacecReqNull",
        "AdvAssaultTarget",
        "Utility peace",
        "PatrolFollower",
        "PatrolAssault"
    };

    private const string FightRequestLayerPrefix = "FiReq:";
    private const string FightRequestLayerWithoutRequest = "FightReqNull";
    private const string PeacefulRequestLayerPrefix = "PcReq:";
    private const string PeacefulRequestLayerWithoutRequest = "PeacecReqNull";

    private static readonly List<ModulePatch> PatchReferences = new List<ModulePatch>();
    private static readonly List<ScourgeLayerMutation> AppliedMutations =
        new List<ScourgeLayerMutation>();
    private static PropertyInfo _sainLayerNamesProperty;
    private static FieldInfo _commonVanillaLayersField;
    private static FieldInfo _commonVanillaBossAndFollowerLayersField;
    private static PropertyInfo _brainManagerInstanceProperty;
    private static FieldInfo _activatedBotsField;
    private static MethodInfo _restoreLayerForBotMethod;
    private static bool _initialized;

    internal static bool Initialize()
    {
        if (_initialized)
        {
            return true;
        }

        if (!Chainloader.PluginInfos.ContainsKey(ModIdentity.SainGuid))
        {
            _initialized = true;
            return true;
        }

        try
        {
            var fallbackTargets = ResolveFallbackTargets();
            _brainManagerInstanceProperty = fallbackTargets.BrainManagerInstanceProperty;
            _activatedBotsField = fallbackTargets.ActivatedBotsField;
            _restoreLayerForBotMethod = fallbackTargets.RestoreLayerForBotMethod;

            var targets = ResolveTargets();
            _sainLayerNamesProperty = targets.SainLayerNamesProperty;
            _commonVanillaLayersField = targets.CommonVanillaLayersField;
            _commonVanillaBossAndFollowerLayersField =
                targets.CommonVanillaBossAndFollowerLayersField;

            // Keep every constructed patch alive, including if a later Enable
            // call fails. Ready stays false until custom layers are registered.
            PatchReferences.Add(new SainBotSpawnExclusionPatch(targets.IsExcludedMethod));
            PatchReferences.Add(new SainToggleVanillaLayersPatch(targets.ToggleVanillaLayersMethod));
            PatchReferences.Add(new SainToggleVanillaLayersForRolesPatch(
                targets.ToggleVanillaLayersForRolesMethod));

            for (var index = 0; index < PatchReferences.Count; index++)
            {
                PatchReferences[index].Enable();
            }

            _initialized = true;
            return true;
        }
        catch (Exception exception)
        {
            ScourgeClientPlugin.LogSource.LogError(
                $"[Scourge] SAIN compatibility could not initialize; existing AI configuration is retained: {exception}");
            return false;
        }
    }

    // Called after ScourgeAiRuntime marks its custom layers ready. This repairs
    // exclusions created before the compatibility postfixes were active, but
    // only for the two Scourge roles and the ArenaFighter brain.
    internal static bool CompleteInitialization()
    {
        if (!_initialized)
        {
            return false;
        }

        if (!ScourgeAiRuntime.Ready || _sainLayerNamesProperty == null)
        {
            return true;
        }

        try
        {
            RestoreExistingScourgeExclusions();
            return true;
        }
        catch (Exception exception)
        {
            FailClosed(
                "SAIN scoped layer repair was not safe to apply; custom tactics disabled and existing AI configuration retained",
                exception);
            ScourgeClientPlugin.LogSource.LogError(
                $"[Scourge] SAIN compatibility cleanup failed: {exception}");
            return false;
        }
    }

    private static SainTargets ResolveTargets()
    {
        var pluginInfo = Chainloader.PluginInfos[ModIdentity.SainGuid];
        var sainAssembly = pluginInfo.Instance?.GetType().Assembly;
        if (sainAssembly == null)
        {
            sainAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .AsValueEnumerable()
                .FirstOrDefault(assembly =>
                    assembly.GetType(SainBotSpawnControllerTypeName, false) != null);
        }

        if (sainAssembly == null)
        {
            throw new InvalidOperationException("The loaded SAIN assembly could not be located.");
        }

        var botSpawnControllerType = sainAssembly.GetType(
            SainBotSpawnControllerTypeName,
            true);
        var isExcludedMethod = botSpawnControllerType.GetMethod(
            "IsExcluded",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(WildSpawnType) },
            null);
        RequireMethod(
            isExcludedMethod,
            botSpawnControllerType,
            "IsExcluded(WildSpawnType)",
            typeof(bool));

        var brainAssignmentType = sainAssembly.GetType(SainBrainAssignmentTypeName, true);
        var toggleVanillaLayersMethod = brainAssignmentType.GetMethod(
            "ToggleVanillaLayers",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(List<string>), typeof(List<string>), typeof(bool) },
            null);
        RequireMethod(
            toggleVanillaLayersMethod,
            brainAssignmentType,
            "ToggleVanillaLayers(List<string>, List<string>, bool)",
            typeof(void));

        var toggleVanillaLayersForRolesMethod = brainAssignmentType.GetMethod(
            "ToggleVanillaLayers",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[]
            {
                typeof(List<string>),
                typeof(List<string>),
                typeof(List<WildSpawnType>),
                typeof(bool)
            },
            null);
        RequireMethod(
            toggleVanillaLayersForRolesMethod,
            brainAssignmentType,
            "ToggleVanillaLayers(List<string>, List<string>, List<WildSpawnType>, bool)",
            typeof(void));

        var bigBrainHandlerType = sainAssembly.GetType(SainBigBrainHandlerTypeName, true);
        var sainLayerNamesProperty = bigBrainHandlerType.GetProperty(
            SainLayerNamesPropertyName,
            BindingFlags.Public | BindingFlags.Static);
        if (sainLayerNamesProperty == null
            || sainLayerNamesProperty.DeclaringType != bigBrainHandlerType
            || sainLayerNamesProperty.PropertyType != typeof(List<string>)
            || sainLayerNamesProperty.GetMethod == null
            || !sainLayerNamesProperty.GetMethod.IsStatic)
        {
            throw new MissingMemberException(
                bigBrainHandlerType.FullName,
                SainLayerNamesPropertyName);
        }

        var commonVanillaLayersField = RequireStringArrayField(
            bigBrainHandlerType,
            CommonVanillaLayersFieldName);
        var commonVanillaBossAndFollowerLayersField = RequireStringArrayField(
            bigBrainHandlerType,
            CommonVanillaBossAndFollowerLayersFieldName);

        return new SainTargets(
            isExcludedMethod,
            toggleVanillaLayersMethod,
            toggleVanillaLayersForRolesMethod,
            sainLayerNamesProperty,
            commonVanillaLayersField,
            commonVanillaBossAndFollowerLayersField);
    }

    private static FallbackTargets ResolveFallbackTargets()
    {
        var brainManagerType = typeof(BrainManager);
        var brainManagerInstanceProperty = brainManagerType.GetProperty(
            "Instance",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (brainManagerInstanceProperty == null
            || brainManagerInstanceProperty.DeclaringType != brainManagerType
            || brainManagerInstanceProperty.PropertyType != brainManagerType
            || brainManagerInstanceProperty.GetMethod == null
            || !brainManagerInstanceProperty.GetMethod.IsStatic)
        {
            throw new MissingMemberException(brainManagerType.FullName, "Instance");
        }

        var activatedBotsField = brainManagerType.GetField(
            "ActivatedBots",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (activatedBotsField == null
            || activatedBotsField.DeclaringType != brainManagerType
            || activatedBotsField.FieldType != typeof(Dictionary<IPlayer, BotOwner>))
        {
            throw new MissingFieldException(brainManagerType.FullName, "ActivatedBots");
        }

        var brainHelpersType = brainManagerType.Assembly.GetType(
            BigBrainHelpersTypeName,
            true);
        var restoreLayerForBotMethod = brainHelpersType.GetMethod(
            "RestoreLayerForBot",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(BotOwner), typeof(string) },
            null);
        if (restoreLayerForBotMethod == null
            || restoreLayerForBotMethod.DeclaringType != brainHelpersType
            || !restoreLayerForBotMethod.IsStatic
            || restoreLayerForBotMethod.ReturnType != typeof(void))
        {
            throw new MissingMethodException(
                brainHelpersType.FullName,
                "RestoreLayerForBot(BotOwner, string)");
        }

        return new FallbackTargets(
            brainManagerInstanceProperty,
            activatedBotsField,
            restoreLayerForBotMethod);
    }

    private static FieldInfo RequireStringArrayField(Type declaringType, string fieldName)
    {
        var field = declaringType.GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null
            || field.DeclaringType != declaringType
            || !field.IsStatic
            || field.FieldType != typeof(string[]))
        {
            throw new MissingFieldException(declaringType.FullName, fieldName);
        }

        return field;
    }

    private static void RequireMethod(
        MethodInfo method,
        Type declaringType,
        string methodName,
        Type returnType)
    {
        if (method == null
            || method.DeclaringType != declaringType
            || !method.IsStatic
            || method.ReturnType != returnType)
        {
            throw new MissingMethodException(declaringType.FullName, methodName);
        }
    }

    private static void ApplyForSuppliedScopes(
        List<string> brainNames,
        List<string> layerNames,
        List<WildSpawnType> roles)
    {
        if (!ScourgeAiRuntime.Ready
            || brainNames == null
            || brainNames.Count == 0
            || roles == null
            || roles.Count == 0)
        {
            return;
        }

        var selectedRoles = roles.AsValueEnumerable()
            .Where(role => ScourgeRoles.IsScourge((int)role))
            .Distinct()
            .ToList();
        if (selectedRoles.Count == 0)
        {
            return;
        }

        var scopes = BuildScopes(brainNames, selectedRoles);
        try
        {
            ApplyScopes(scopes, layerNames);
        }
        catch (Exception exception)
        {
            FailClosed(
                "SAIN late layer repair was not safe to apply; custom tactics disabled and existing AI configuration retained",
                exception);
        }
    }

    private static List<ScourgeScope> BuildScopes(
        List<string> brainNames,
        List<WildSpawnType> selectedRoles)
    {
        var scopes = new List<ScourgeScope>();
        for (var index = 0; index < selectedRoles.Count; index++)
        {
            var role = selectedRoles[index];
            if (!brainNames.AsValueEnumerable().Contains(ArenaFighterBrainName))
            {
                continue;
            }

            var scope = scopes.AsValueEnumerable()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.BrainName,
                    ArenaFighterBrainName,
                    StringComparison.Ordinal));

            if (scope == null)
            {
                scope = new ScourgeScope(ArenaFighterBrainName);
                scopes.Add(scope);
            }

            scope.Roles.Add(role);
        }

        return scopes;
    }

    private static void ApplyScopes(
        List<ScourgeScope> scopes,
        List<string> layerNames)
    {
        if (scopes.Count == 0)
        {
            return;
        }

        var sainLayerNames = ReadSainLayerNames();
        // SAIN can toggle a subset of its layers after startup. Only carry
        // verified ArenaFighter names from a late callback into preflight;
        // SAIN/MoreBots global names such as GroupForce are foreign scope.
        var vanillaLayerNames = ReadNativeLayerNames();
        if (layerNames != null)
        {
            vanillaLayerNames.AddRange(layerNames.AsValueEnumerable()
                .Where(name =>
                    IsArenaFighterNativeLayerName(name)
                    && !sainLayerNames.AsValueEnumerable().Contains(name))
                .Distinct()
                .ToList());
        }

        vanillaLayerNames = vanillaLayerNames.AsValueEnumerable()
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToList();

        ApplyPlans(scopes, vanillaLayerNames, sainLayerNames);
    }

    private static void RestoreExistingScourgeExclusions()
    {
        var scopes = BuildScopes(
            new List<string> { ArenaFighterBrainName },
            ScourgeAiRuntime.Roles.AsValueEnumerable().ToList());
        if (scopes.Count == 0)
        {
            return;
        }

        var sainLayerNames = ReadSainLayerNames();
        var nativeLayerNames = ReadNativeLayerNames();
        ApplyPlans(scopes, nativeLayerNames, sainLayerNames);
    }

    private static void ApplyPlans(
        List<ScourgeScope> scopes,
        List<string> candidateVanillaLayerNames,
        List<string> sainLayerNames)
    {
        // Every scope is inspected before the first BrainManager mutation. A
        // native exclusion broad on both axes cannot be split without risking
        // a shared role, so one unresolved scope disables the whole transaction.
        var plans = BuildPlans(
            scopes,
            candidateVanillaLayerNames,
            sainLayerNames);
        if (plans.Count == 0)
        {
            return;
        }

        var transaction = new List<ScourgeLayerMutation>();
        try
        {
            for (var planIndex = 0; planIndex < plans.Count; planIndex++)
            {
                ApplyScopedLayers(plans[planIndex], transaction);
            }

            AppliedMutations.AddRange(transaction);
        }
        catch
        {
            RollbackMutations(transaction);
            throw;
        }
    }

    private static void ApplyScopedLayers(
        ScourgeLayerPlan plan,
        List<ScourgeLayerMutation> transaction)
    {
        var brains = new List<string>(1) { plan.BrainName };
        var roles = new List<WildSpawnType>(1) { plan.Role };
        for (var nativeIndex = 0;
             nativeIndex < plan.NativeLayersToRestore.Count;
             nativeIndex++)
        {
            var mutation = ScourgeLayerMutation.ForNativeRestore(
                plan.NativeLayersToRestore[nativeIndex],
                plan.BrainName,
                plan.Role);
            transaction.Add(mutation);
            BrainManager.RestoreLayers(
                new List<string>(1) { mutation.LayerName },
                brains,
                roles);
        }

        for (var sainIndex = 0;
             sainIndex < plan.SainLayersToRemove.Count;
             sainIndex++)
        {
            var mutation = ScourgeLayerMutation.ForSainRemoval(
                plan.SainLayersToRemove[sainIndex],
                plan.BrainName,
                plan.Role);
            var preexistingExclusions = FindExactExclusions(
                mutation.LayerName,
                mutation.BrainName,
                mutation.Role);
            transaction.Add(mutation);
            try
            {
                BrainManager.RemoveLayers(
                    new List<string>(1) { mutation.LayerName },
                    brains,
                    roles);
            }
            finally
            {
                // BigBrain inserts the global exclusion before updating live
                // bots. Capture ownership even if a live-bot update throws.
                mutation.CreatedExclusion = FindCreatedExclusion(
                    mutation.LayerName,
                    mutation.BrainName,
                    mutation.Role,
                    preexistingExclusions);
            }
        }
    }

    private static List<string> ReadSainLayerNames()
    {
        // The backing field is populated lazily by SAIN. Calling its public
        // property is required so MoreBots and late preset loads are included.
        var layerNames = _sainLayerNamesProperty?.GetValue(null, null) as List<string>;
        return layerNames == null
            ? new List<string>()
            : layerNames.AsValueEnumerable()
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .ToList();
    }

    private static List<string> ReadNativeLayerNames()
    {
        var layerNames = ArenaFighterNativeLayerNames.AsValueEnumerable().ToList();

        // FightRequestLayer and PeacefulRequestLayer return a request-specific
        // name. Include only an actual exclusion carrying one of those names;
        // do not invent a broad prefix entry for BrainManager.
        var exclusions = BrainManager.ExcludeLayersReadOnly;
        if (exclusions != null)
        {
            layerNames.AddRange(exclusions.AsValueEnumerable()
                .Where(exclusion => exclusion != null
                    && IsArenaFighterNativeLayerName(exclusion.excludeLayerName))
                .Select(exclusion => exclusion.excludeLayerName)
                .ToList());
        }

        return layerNames.AsValueEnumerable()
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToList();
    }

    private static bool IsArenaFighterNativeLayerName(string layerName)
    {
        if (string.IsNullOrEmpty(layerName))
        {
            return false;
        }

        return ArenaFighterNativeLayerNames.AsValueEnumerable().Contains(layerName)
            || string.Equals(
                layerName,
                FightRequestLayerWithoutRequest,
                StringComparison.Ordinal)
            || layerName.Length > FightRequestLayerPrefix.Length
                && layerName.StartsWith(FightRequestLayerPrefix, StringComparison.Ordinal)
            || string.Equals(
                layerName,
                PeacefulRequestLayerWithoutRequest,
                StringComparison.Ordinal)
            || layerName.Length > PeacefulRequestLayerPrefix.Length
                && layerName.StartsWith(PeacefulRequestLayerPrefix, StringComparison.Ordinal);
    }

    private static List<ScourgeLayerPlan> BuildPlans(
        List<ScourgeScope> scopes,
        List<string> candidateVanillaLayerNames,
        List<string> sainLayerNames)
    {
        var plans = new List<ScourgeLayerPlan>();
        var exclusions = BrainManager.ExcludeLayersReadOnly;
        var nativeLayers = candidateVanillaLayerNames ?? new List<string>();
        var saInLayers = sainLayerNames ?? new List<string>();

        for (var scopeIndex = 0; scopeIndex < scopes.Count; scopeIndex++)
        {
            var scope = scopes[scopeIndex];
            for (var roleIndex = 0; roleIndex < scope.Roles.Count; roleIndex++)
            {
                var role = scope.Roles[roleIndex];
                var plan = new ScourgeLayerPlan(scope.BrainName, role);
                plan.NativeLayersToRestore.AddRange(FindNativeLayersToRestore(
                    nativeLayers,
                    exclusions,
                    scope.BrainName,
                    role));

                plan.SainLayersToRemove.AddRange(saInLayers.AsValueEnumerable()
                    .Where(layerName =>
                        !string.IsNullOrEmpty(layerName)
                        && !HasRelevantExclusion(
                            exclusions,
                            layerName,
                            scope.BrainName,
                            role))
                    .Distinct()
                    .ToList());

                if (plan.NativeLayersToRestore.Count > 0
                    || plan.SainLayersToRemove.Count > 0)
                {
                    plans.Add(plan);
                }
            }
        }

        return plans;
    }

    private static List<string> FindNativeLayersToRestore(
        List<string> candidateLayerNames,
        IReadOnlyList<BrainManager.ExcludeLayerInfo> exclusions,
        string brainName,
        WildSpawnType role)
    {
        var result = new List<string>();
        if (exclusions == null || exclusions.Count == 0)
        {
            return result;
        }

        for (var layerIndex = 0; layerIndex < candidateLayerNames.Count; layerIndex++)
        {
            var layerName = candidateLayerNames[layerIndex];
            if (string.IsNullOrEmpty(layerName))
            {
                continue;
            }

            var matching = exclusions.AsValueEnumerable()
                .Where(exclusion => HasRelevantExclusion(
                    exclusion,
                    layerName,
                    brainName,
                    role))
                .ToList();
            if (matching.Count == 0)
            {
                continue;
            }

            if (!HasSafelySplittableExclusion(
                    exclusions,
                    layerName,
                    brainName,
                    role))
            {
                throw new InvalidOperationException(
                    $"SAIN native exclusion '{layerName}' is broad for ArenaFighter role {(int)role}; refusing to mutate shared roles.");
            }

            result.Add(layerName);
        }

        return result;
    }

    private static bool HasSafelySplittableExclusion(
        IReadOnlyList<BrainManager.ExcludeLayerInfo> exclusions,
        string layerName,
        string brainName,
        WildSpawnType role)
    {
        var matchingExclusions = exclusions.AsValueEnumerable()
            .Where(exclusion => HasRelevantExclusion(
                exclusion,
                layerName,
                brainName,
                role))
            .ToList();

        // BigBrain can split one axis at a time. A matching exclusion is safe
        // when either the brain axis is exactly ArenaFighter (roles may be
        // shared) or the role axis is exactly this Scourge role (brains may be
        // shared). A null or multi-axis entry cannot be isolated safely.
        return matchingExclusions.Count > 0
            && !matchingExclusions.AsValueEnumerable().Any(exclusion =>
                !IsSafelySplittableExclusion(exclusion, brainName, role));
    }

    private static bool HasRelevantExclusion(
        IReadOnlyList<BrainManager.ExcludeLayerInfo> exclusions,
        string layerName,
        string brainName,
        WildSpawnType role)
    {
        if (exclusions == null || exclusions.Count == 0)
        {
            return false;
        }

        return exclusions.AsValueEnumerable()
            .Any(exclusion => HasRelevantExclusion(exclusion, layerName, brainName, role));
    }

    private static bool HasRelevantExclusion(
        BrainManager.ExcludeLayerInfo exclusion,
        string layerName,
        string brainName,
        WildSpawnType role)
    {
        if (exclusion == null
            || !string.Equals(
                exclusion.excludeLayerName,
                layerName,
                StringComparison.Ordinal))
        {
            return false;
        }

        return CouldContain(exclusion.ExcludeLayerBrains, brainName)
            && CouldContain(exclusion.ExcludeLayerRoles, role);
    }

    private static bool CouldContain<T>(IReadOnlyList<T> values, T value)
    {
        if (values == null)
        {
            // Null is an unresolved wildcard in a third-party exclusion. It
            // must be treated as relevant so compatibility fails closed.
            return true;
        }

        return values.Count > 0 && values.AsValueEnumerable().Contains(value);
    }

    private static bool IsSafelySplittableExclusion(
        BrainManager.ExcludeLayerInfo exclusion,
        string brainName,
        WildSpawnType role)
    {
        var brains = exclusion?.ExcludeLayerBrains;
        var roles = exclusion?.ExcludeLayerRoles;
        if (brains == null || roles == null || brains.Count == 0 || roles.Count == 0)
        {
            return false;
        }

        var exactBrain = brains.Count == 1
            && string.Equals(brains[0], brainName, StringComparison.Ordinal);
        var exactRole = roles.Count == 1
            && EqualityComparer<WildSpawnType>.Default.Equals(roles[0], role);
        return exactBrain && CouldContain(roles, role)
            || exactRole && CouldContain(brains, brainName);
    }

    private static List<BrainManager.ExcludeLayerInfo> FindExactExclusions(
        string layerName,
        string brainName,
        WildSpawnType role)
    {
        var exclusions = BrainManager.ExcludeLayersReadOnly;
        if (exclusions == null)
        {
            return new List<BrainManager.ExcludeLayerInfo>();
        }

        return exclusions.AsValueEnumerable()
            .Where(exclusion => IsExactExclusion(exclusion, layerName, brainName, role))
            .ToList();
    }

    private static BrainManager.ExcludeLayerInfo FindCreatedExclusion(
        string layerName,
        string brainName,
        WildSpawnType role,
        IReadOnlyList<BrainManager.ExcludeLayerInfo> preexistingExclusions)
    {
        var exclusions = FindExactExclusions(layerName, brainName, role);
        return exclusions.AsValueEnumerable()
            .FirstOrDefault(exclusion =>
                preexistingExclusions == null
                || !preexistingExclusions.AsValueEnumerable().Contains(exclusion));
    }

    private static bool IsExactExclusion(
        BrainManager.ExcludeLayerInfo exclusion,
        string layerName,
        string brainName,
        WildSpawnType role)
    {
        return exclusion != null
            && string.Equals(exclusion.excludeLayerName, layerName, StringComparison.Ordinal)
            && exclusion.ExcludeLayerBrains != null
            && exclusion.ExcludeLayerRoles != null
            && exclusion.ExcludeLayerBrains.Count == 1
            && exclusion.ExcludeLayerRoles.Count == 1
            && string.Equals(
                exclusion.ExcludeLayerBrains[0],
                brainName,
                StringComparison.Ordinal)
            && EqualityComparer<WildSpawnType>.Default.Equals(
                exclusion.ExcludeLayerRoles[0],
                role);
    }

    private static void FailClosed(string reason, Exception exception)
    {
        var liveScourgeBots = CaptureLiveScourgeBots();
        RollbackMutations(AppliedMutations);
        AppliedMutations.Clear();
        ScourgeAiRuntime.Disable(reason);
        RestoreNativeLayersForBots(liveScourgeBots);
        ScourgeClientPlugin.LogSource.LogError(
            $"[Scourge] {reason}: {exception}");
    }

    private static List<BotOwner> CaptureLiveScourgeBots()
    {
        var result = new List<BotOwner>();
        if (_brainManagerInstanceProperty == null || _activatedBotsField == null)
        {
            return result;
        }

        try
        {
            var brainManager = _brainManagerInstanceProperty.GetValue(null, null) as BrainManager;
            var activatedBots = _activatedBotsField.GetValue(brainManager)
                as Dictionary<IPlayer, BotOwner>;
            if (activatedBots == null)
            {
                return result;
            }

            return activatedBots.Values.AsValueEnumerable()
                .Where(bot => bot != null
                    && !bot.IsDead
                    && bot.Profile?.Info?.Settings != null
                    && ScourgeRoles.IsScourge((int)bot.Profile.Info.Settings.Role))
                .Distinct()
                .ToList();
        }
        catch (Exception captureException)
        {
            ScourgeClientPlugin.LogSource.LogError(
                $"[Scourge] could not snapshot live Scourge bots for native fallback: {captureException}");
            return result;
        }
    }

    private static void RestoreNativeLayersForBots(IReadOnlyList<BotOwner> bots)
    {
        if (_restoreLayerForBotMethod == null || bots == null || bots.Count == 0)
        {
            return;
        }

        List<string> nativeLayerNames;
        try
        {
            nativeLayerNames = ReadNativeLayerNames();
        }
        catch (Exception readException)
        {
            ScourgeClientPlugin.LogSource.LogError(
                $"[Scourge] native fallback layer list could not be read: {readException}");
            return;
        }
        for (var botIndex = 0; botIndex < bots.Count; botIndex++)
        {
            var bot = bots[botIndex];
            if (bot == null || bot.IsDead)
            {
                continue;
            }

            for (var layerIndex = 0; layerIndex < nativeLayerNames.Count; layerIndex++)
            {
                var layerName = nativeLayerNames[layerIndex];
                try
                {
                    _restoreLayerForBotMethod.Invoke(
                        null,
                        new object[] { bot, layerName });
                }
                catch (Exception restoreException)
                {
                    ScourgeClientPlugin.LogSource.LogError(
                        $"[Scourge] native fallback could not restore '{layerName}' for live Scourge bot {bot.ProfileId}: {restoreException}");
                }
            }
        }
    }

    private static void RollbackMutations(IReadOnlyList<ScourgeLayerMutation> mutations)
    {
        if (mutations == null)
        {
            return;
        }

        for (var index = mutations.Count - 1; index >= 0; index--)
        {
            var mutation = mutations[index];
            try
            {
                var brains = new List<string>(1) { mutation.BrainName };
                var roles = new List<WildSpawnType>(1) { mutation.Role };
                if (mutation.Kind == ScourgeLayerMutationKind.NativeRestore)
                {
                    BrainManager.RemoveLayers(
                        new List<string>(1) { mutation.LayerName },
                        brains,
                        roles);
                }
                else if (mutation.CreatedExclusion != null
                    && IsOnlyMatchingExclusion(mutation))
                {
                    // The SAIN exclusion was created by this transaction. If
                    // another owner now covers the same scope, leave it alone
                    // instead of restoring through that foreign exclusion.
                    BrainManager.RestoreLayers(
                        new List<string>(1) { mutation.LayerName },
                        brains,
                        roles);
                }
            }
            catch (Exception exception)
            {
                ScourgeClientPlugin.LogSource.LogError(
                    $"[Scourge] SAIN compatibility rollback failed for '{mutation.LayerName}': {exception}");
            }
        }
    }

    private static bool IsOnlyMatchingExclusion(ScourgeLayerMutation mutation)
    {
        var exclusions = BrainManager.ExcludeLayersReadOnly;
        if (exclusions == null)
        {
            return false;
        }

        var matching = exclusions.AsValueEnumerable()
            .Where(exclusion => HasRelevantExclusion(
                exclusion,
                mutation.LayerName,
                mutation.BrainName,
                mutation.Role))
            .ToList();
        return matching.Count == 1
            && ReferenceEquals(matching[0], mutation.CreatedExclusion);
    }

    private static void AddReflectedLayerNames(List<string> destination, FieldInfo source)
    {
        var layerNames = source?.GetValue(null) as string[];
        if (layerNames != null)
        {
            destination.AddRange(layerNames);
        }
    }

    private sealed class ScourgeLayerPlan
    {
        internal ScourgeLayerPlan(string brainName, WildSpawnType role)
        {
            BrainName = brainName;
            Role = role;
            NativeLayersToRestore = new List<string>();
            SainLayersToRemove = new List<string>();
        }

        internal string BrainName { get; }
        internal WildSpawnType Role { get; }
        internal List<string> NativeLayersToRestore { get; }
        internal List<string> SainLayersToRemove { get; }
    }

    private enum ScourgeLayerMutationKind
    {
        NativeRestore,
        SainRemoval
    }

    private sealed class ScourgeLayerMutation
    {
        private ScourgeLayerMutation(
            string layerName,
            string brainName,
            WildSpawnType role,
            ScourgeLayerMutationKind kind)
        {
            LayerName = layerName;
            BrainName = brainName;
            Role = role;
            Kind = kind;
        }

        internal string LayerName { get; }
        internal string BrainName { get; }
        internal WildSpawnType Role { get; }
        internal ScourgeLayerMutationKind Kind { get; }
        internal BrainManager.ExcludeLayerInfo CreatedExclusion { get; set; }

        internal static ScourgeLayerMutation ForNativeRestore(
            string layerName,
            string brainName,
            WildSpawnType role) => new ScourgeLayerMutation(
                layerName,
                brainName,
                role,
                ScourgeLayerMutationKind.NativeRestore);

        internal static ScourgeLayerMutation ForSainRemoval(
            string layerName,
            string brainName,
            WildSpawnType role) => new ScourgeLayerMutation(
                layerName,
                brainName,
                role,
                ScourgeLayerMutationKind.SainRemoval);
    }

    private sealed class SainTargets
    {
        internal SainTargets(
            MethodInfo isExcludedMethod,
            MethodInfo toggleVanillaLayersMethod,
            MethodInfo toggleVanillaLayersForRolesMethod,
            PropertyInfo sainLayerNamesProperty,
            FieldInfo commonVanillaLayersField,
            FieldInfo commonVanillaBossAndFollowerLayersField)
        {
            IsExcludedMethod = isExcludedMethod;
            ToggleVanillaLayersMethod = toggleVanillaLayersMethod;
            ToggleVanillaLayersForRolesMethod = toggleVanillaLayersForRolesMethod;
            SainLayerNamesProperty = sainLayerNamesProperty;
            CommonVanillaLayersField = commonVanillaLayersField;
            CommonVanillaBossAndFollowerLayersField = commonVanillaBossAndFollowerLayersField;
        }

        internal MethodInfo IsExcludedMethod { get; }
        internal MethodInfo ToggleVanillaLayersMethod { get; }
        internal MethodInfo ToggleVanillaLayersForRolesMethod { get; }
        internal PropertyInfo SainLayerNamesProperty { get; }
        internal FieldInfo CommonVanillaLayersField { get; }
        internal FieldInfo CommonVanillaBossAndFollowerLayersField { get; }
    }

    private sealed class FallbackTargets
    {
        internal FallbackTargets(
            PropertyInfo brainManagerInstanceProperty,
            FieldInfo activatedBotsField,
            MethodInfo restoreLayerForBotMethod)
        {
            BrainManagerInstanceProperty = brainManagerInstanceProperty;
            ActivatedBotsField = activatedBotsField;
            RestoreLayerForBotMethod = restoreLayerForBotMethod;
        }

        internal PropertyInfo BrainManagerInstanceProperty { get; }
        internal FieldInfo ActivatedBotsField { get; }
        internal MethodInfo RestoreLayerForBotMethod { get; }
    }

    private sealed class ScourgeScope
    {
        internal ScourgeScope(string brainName)
        {
            BrainName = brainName;
            Roles = new List<WildSpawnType>();
        }

        internal string BrainName { get; }
        internal List<WildSpawnType> Roles { get; }
    }

    private sealed class SainBotSpawnExclusionPatch : ModulePatch
    {
        private readonly MethodBase _targetMethod;

        internal SainBotSpawnExclusionPatch(MethodBase targetMethod)
        {
            _targetMethod = targetMethod;
        }

        protected override MethodBase GetTargetMethod() => _targetMethod;

        [PatchPostfix]
        private static void PatchPostfix(WildSpawnType type, ref bool __result)
        {
            if (ScourgeAiRuntime.Ready && ScourgeRoles.IsScourge((int)type))
            {
                __result = true;
            }
        }
    }

    private sealed class SainToggleVanillaLayersPatch : ModulePatch
    {
        private readonly MethodBase _targetMethod;

        internal SainToggleVanillaLayersPatch(MethodBase targetMethod)
        {
            _targetMethod = targetMethod;
        }

        protected override MethodBase GetTargetMethod() => _targetMethod;

        [PatchPostfix]
        private static void PatchPostfix(
            List<string> brainNames,
            List<string> layerNames,
            bool useVanillaLayers)
        {
            ApplyForSuppliedScopes(
                brainNames,
                layerNames,
                ScourgeAiRuntime.Roles.AsValueEnumerable().ToList());
        }
    }

    private sealed class SainToggleVanillaLayersForRolesPatch : ModulePatch
    {
        private readonly MethodBase _targetMethod;

        internal SainToggleVanillaLayersForRolesPatch(MethodBase targetMethod)
        {
            _targetMethod = targetMethod;
        }

        protected override MethodBase GetTargetMethod() => _targetMethod;

        [PatchPostfix]
        private static void PatchPostfix(
            List<string> brainNames,
            List<string> layerNames,
            List<WildSpawnType> roles,
            bool useVanillaLayers)
        {
            ApplyForSuppliedScopes(brainNames, layerNames, roles);
        }
    }
}
