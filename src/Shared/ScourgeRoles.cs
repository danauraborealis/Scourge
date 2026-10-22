namespace Manimal.Scourge;

public static class ScourgeRoles
{
    public const string Boss = "bossScourgeEventLab";
    public const string Follower = "followerScourgeEventLab";
    public const int BossValue = 776720;
    public const int FollowerValue = 776721;

    public static bool IsScourge(int role) => role is BossValue or FollowerValue;
}
