using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace Manimal.Scourge.Server;

// The customization must exist before MoreBots loads and validates Scourge's appearance.
[Injectable(TypePriority = OnLoadOrder.Preload + 19)]
public sealed class ScourgeVoiceRegistration(
    TemplateTable templateTable,
    JsonUtil jsonUtil,
    ISptLogger<ScourgeVoiceRegistration> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var bundle = Path.Combine(modDirectory, "bundles", ScourgeVoice.BundlePath);
        if (!File.Exists(bundle))
        {
            throw new FileNotFoundException("[Scourge] The Scourge voice bundle is missing; reinstall the complete mod package.", bundle);
        }

        var voiceId = new MongoId(ScourgeVoice.Id);
        if (!templateTable.Customization.TryGetValue(voiceId, out var voice))
        {
            var path = Path.Combine(modDirectory, "db", "voices", "bossscourge.json");
            voice = jsonUtil.Deserialize<CustomizationItem>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"[Scourge] Invalid voice customization: {path}");
            ValidateVoice(voice);
            templateTable.Customization.Add(voiceId, voice);
        }
        else
        {
            // Preserve a compatible customization supplied by another content mod.
            ValidateVoice(voice);
        }

        logger.Info("[Scourge] original BossScourge voice registered.");
        return Task.CompletedTask;
    }

    private static void ValidateVoice(CustomizationItem voice)
    {
        if (voice.Id.ToString() != ScourgeVoice.Id
            || voice.Parent.ToString() != "5fc100cf95572123ae738483"
            || voice.Name != ScourgeVoice.Name
            || voice.Properties?.Prefab?.ToString() != ScourgeVoice.Name)
        {
            throw new InvalidDataException("[Scourge] The Scourge voice customization has an incompatible ID, type, or voice-bank name.");
        }
    }
}
