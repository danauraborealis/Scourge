using System.Reflection;
using SPTarkov.Reflection.Patching;

namespace Manimal.Scourge.Server;

// BPS replaces both the full database and individual maps after raids/settings changes.
// Restore only this mod's wave, after either replacement, through SPT's patch lifecycle.
internal sealed class BpsSpawnRestorePatch : AbstractPatch
{
    private readonly MethodBase _method;
    private static Action? _restore;

    public BpsSpawnRestorePatch(MethodBase method, Action restore)
        : base($"{ModIdentity.Guid}.bps.{method.Name}")
    {
        _method = method;
        _restore = restore;
    }

    protected override MethodBase GetTargetMethod() => _method;

    [PatchPostfix]
    private static void Postfix() => _restore?.Invoke();
}
