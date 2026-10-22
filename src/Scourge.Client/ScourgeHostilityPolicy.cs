using Manimal.Scourge;

namespace Manimal.Scourge.Client;

internal static class ScourgeHostilityPolicy
{
    internal static bool IsScourgePair(int groupRole, int targetRole) =>
        ScourgeRoles.IsScourge(groupRole) && ScourgeRoles.IsScourge(targetRole);

    // Only Scourge AI roles share allegiance. Player scavs and unknown custom
    // factions are outsiders; pairs with no Scourge involvement stay native.
    internal static bool TryGetEnemyDecision(
        int groupRole, bool targetIsAi, int targetRole, out bool isEnemy)
    {
        var groupIsScourge = ScourgeRoles.IsScourge(groupRole);
        var targetIsScourge = targetIsAi && ScourgeRoles.IsScourge(targetRole);
        isEnemy = groupIsScourge != targetIsScourge;
        return groupIsScourge || targetIsScourge;
    }
}
