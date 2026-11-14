using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Scourge.Server;

// the voice and its bundle ship with wtt contentbackport, which loads at Preload + 2.
// fail here with a readable message, before morebots validates the boss appearance.
[Injectable(TypePriority = OnLoadOrder.Preload + 19)]
public sealed class ScourgeVoiceCheck(
    TemplateTable templateTable,
    ISptLogger<ScourgeVoiceCheck> logger) : IOnLoad
{
    private const string VoiceParent = "5fc100cf95572123ae738483";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!templateTable.Customization.TryGetValue(new MongoId(ScourgeVoice.Id), out var voice) || voice is null)
        {
            throw new InvalidOperationException(
                $"[Scourge] voice '{ScourgeVoice.Name}' ({ScourgeVoice.Id}) is not installed. It ships with WTT ContentBackport {ModIdentity.WttVersion} or newer.");
        }

        if (voice.Parent.ToString() != VoiceParent
            || voice.Name != ScourgeVoice.Name
            || voice.Properties?.Prefab?.ToString() != ScourgeVoice.Name)
        {
            throw new InvalidDataException("[Scourge] the installed Scourge voice customization has an incompatible type or voice-bank name.");
        }

        logger.Info($"[Scourge] {ScourgeVoice.Name} voice supplied by WTT ContentBackport.");
        return Task.CompletedTask;
    }
}
