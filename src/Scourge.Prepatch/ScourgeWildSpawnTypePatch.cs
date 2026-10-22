using System;
using System.Collections.Generic;
using Mono.Cecil;
using MoreBotsAPI;

namespace Manimal.Scourge.Prepatch;

/// <summary>
/// Adds the Scourge boss and follower enum values before EFT loads Assembly-CSharp.
/// MoreBotsAPI supplies the runtime handling for custom roles and their brains.
/// </summary>
public static class ScourgeWildSpawnTypePatch
{
    private static readonly List<int> AllowedDifficulties = new();

    private static readonly List<int> ScourgeGroup = new()
    {
        ScourgeRoles.BossValue,
        ScourgeRoles.FollowerValue,
    };

    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    public static void Patch(ref AssemblyDefinition assembly)
    {
        var wildSpawnType = assembly.MainModule.GetType("EFT.WildSpawnType")
            ?? throw new InvalidOperationException("EFT.WildSpawnType was not found in Assembly-CSharp.");

        // Check both roles before modifying the in-memory assembly.
        ValidateRole(wildSpawnType, ScourgeRoles.BossValue, ScourgeRoles.Boss);
        ValidateRole(wildSpawnType, ScourgeRoles.FollowerValue, ScourgeRoles.Follower);
        RegisterRole(assembly, ScourgeRoles.BossValue, ScourgeRoles.Boss, "Boss", isBoss: true, isFollower: false);
        RegisterRole(assembly, ScourgeRoles.FollowerValue, ScourgeRoles.Follower, "Follower", isBoss: false, isFollower: true);
        CustomWildSpawnTypeManager.AddSuitableGroup(ScourgeGroup);
    }

    private static void ValidateRole(TypeDefinition wildSpawnType, int value, string name)
    {
        // This runs in the BepInEx preloader before Unity plugins are available. The
        // Cecil validation is deliberately kept free of a runtime UnityToolkit
        // dependency; it only prevents an enum collision from corrupting Assembly-CSharp.
        foreach (var field in wildSpawnType.Fields)
        {
            if (field.Name == name)
            {
                throw new InvalidOperationException($"Cannot register Scourge role '{name}': EFT already defines that enum name.");
            }

            if (field.Constant is int existingValue && existingValue == value)
            {
                throw new InvalidOperationException($"Cannot register Scourge role '{name}': enum value {value} is already in use by '{field.Name}'.");
            }
        }
    }

    private static void RegisterRole(AssemblyDefinition assembly, int value, string name, string scavRole, bool isBoss, bool isFollower)
    {
        var customType = new CustomWildSpawnType(
            value,
            name,
            scavRole,
            baseBrain: 34,
            isBoss,
            isFollower,
            isHostileToEverybody: true);
        customType.SetCountAsBossForStatistics(isBoss);
        customType.SetShouldUseFenceNoBossAttack(shouldUseForScav: false, shouldUseForPMC: false);
        customType.SetExcludedDifficulties(AllowedDifficulties);
        CustomWildSpawnTypeManager.RegisterWildSpawnType(customType, assembly);
    }
}
