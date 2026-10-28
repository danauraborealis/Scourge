using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace Manimal.Scourge.Server;

/// <summary>
/// Loads the seven original Scourge event quests as ordinary, permanent quests.
/// The retail event configuration is deliberately not changed: the quest
/// templates and their locale/dialogue keys are registered through the native
/// custom quest service instead.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.GameCallbacks - 2)]
public sealed class ScourgeQuestRegistration(
    CustomQuestService customQuestService,
    TemplateTable templateTable,
    LocaleTable localeTable,
    TradersTable tradersTable,
    QuestConfig questConfig,
    ScourgeQuestStartPatch questStartPatch,
    ImageRouter imageRouter,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    ISptLogger<ScourgeQuestRegistration> logger) : IOnLoad
{
    private const string QuestDataFile = "db/quests/quests.json";

    private static readonly MongoId PreliminarySurveyQuestId = new("6a91840a740be0cff50e0310");
    private static readonly MongoId SideHustleQuestId = new("6ac4c42976842fabb501d5b8");
    private static readonly MongoId DoxedQuestId = new("6ac4c726a3ea5ab42a074d3a");

    private static readonly string[] QuestIds =
    [
        "6a91840a740be0cff50e0310", // Preliminary Survey
        "6ac3bc49f3f4480654003d7a", // Hidden Route
        "6ac3ca960cee4580a601b28c", // A Simple Task in a Difficult Place
        "6ac3d88d14db1a2f3e0787bd", // Extending the Operation
        "6ac4c1d676842fabb501d5aa", // Information Trail
        "6ac4c42976842fabb501d5b8", // Side Hustle
        "6ac4c726a3ea5ab42a074d3a", // Doxed
    ];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var dataPath = Path.Combine(modPath, QuestDataFile.Replace('/', Path.DirectorySeparatorChar));
        var document = jsonUtil.DeserializeFromFile<ScourgeQuestDocument>(dataPath)
            ?? throw new InvalidDataException($"[Scourge] Quest data is missing or invalid: {dataPath}");

        var nativeStartedWith = AdaptQuestConfig();
        var definitions = LoadDefinitions(document, cancellationToken);
        if (!nativeStartedWith)
        {
            AddLegacyDoxedGate(definitions);
            questStartPatch.EnableCompatibility();
        }

        RegisterImages(definitions, modPath, cancellationToken);

        var locales = BuildLocales(document, localeTable.Global.Keys);
        var errors = Preflight(definitions, locales);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "[Scourge] quest registration preflight failed:\n" + string.Join("\n", errors));
        }

        var registered = 0;
        var skipped = 0;
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (templateTable.Quests.ContainsKey(definition.Quest.Id))
            {
                // An existing quest may be supplied by another content mod or
                // a future SPT data set. Never overwrite that in-memory object.
                skipped++;
                logger.Warning($"[Scourge] quest {definition.Quest.Id} already exists; preserving the existing template.");
                continue;
            }

            var result = customQuestService.CreateQuest(new NewQuestDetails
            {
                NewQuest = definition.Quest,
                Locales = locales,
                LockedToSide = null,
            });
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"[Scourge] unable to register quest {definition.Quest.Id}: "
                    + string.Join("; ", result.Errors));
            }

            registered++;
        }

        logger.Info($"[Scourge] registered {registered} original event quests ({skipped} already present and preserved).");
        return Task.CompletedTask;
    }

    private bool AdaptQuestConfig()
    {
        // These properties were added to QuestConfig after the first 4.1.6
        // runtime build. Use the native properties when present, while still
        // allowing this assembly to load against that earlier 4.1.6 build.
        // The reflection fallback is intentionally narrow: it changes only
        // the two quest-gating collections and never touches event dates or
        // the global non-seasonal-event switch.
        var configType = questConfig.GetType();
        var withheldProperty = configType.GetProperty("WithheldQuests");
        var withheld = withheldProperty?.GetValue(questConfig) as ICollection<MongoId>;
        if (withheld is not null)
        {
            // Preliminary Survey is the first quest in this standalone chain.
            withheld.Remove(PreliminarySurveyQuestId);
        }
        else
        {
            logger.Warning(
                "[Scourge] target QuestConfig has no WithheldQuests collection; "
                + "the installed SPT build cannot apply the Preliminary Survey visibility override.");
        }

        var startedWithProperty = configType.GetProperty("StartedWithQuests");
        var startedWith = startedWithProperty?.GetValue(questConfig) as IDictionary<MongoId, MongoId>;
        if (startedWith is not null)
        {
            // This is keyed by the quest that starts. Doxed therefore remains
            // unavailable until Side Hustle is accepted through native flow.
            startedWith[DoxedQuestId] = SideHustleQuestId;
        }
        else
        {
            logger.Warning(
                "[Scourge] target QuestConfig has no StartedWithQuests collection; "
                + "enabling the legacy Doxed/Side Hustle AcceptQuest compatibility path.");
        }

        return startedWith is not null;
    }

    private static void AddLegacyDoxedGate(IReadOnlyList<QuestDefinition> definitions)
    {
        var doxed = definitions.FirstOrDefault(static definition => definition.Id == DoxedQuestId.ToString());
        if (doxed is null)
        {
            return;
        }

        // Older 4.1.6 runtimes do not expose StartedWithQuests or implement
        // StartQuestsStartedWith. Preserve the source template, but prevent
        // its condition-less Doxed quest from appearing immediately there.
        // The native mapping remains authoritative on runtimes that expose it.
        var startConditions = doxed.Quest.Conditions.AvailableForStart ??= [];
        if (startConditions.Any(condition =>
                condition.ConditionType == "Quest"
                && TargetValues(condition.Target).Contains(SideHustleQuestId.ToString(), StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        startConditions.Add(new QuestCondition
        {
            Id = new MongoId("6ac4c726a3ea5ab42a074d3b"),
            Index = startConditions.Count,
            DynamicLocale = false,
            VisibilityConditions = [],
            ParentId = null,
            Target = new ListOrT<string>(null, SideHustleQuestId.ToString()),
            Status = [QuestStatusEnum.Started, QuestStatusEnum.Success],
            AvailableAfter = 0,
            Dispersion = 0,
            ConditionType = "Quest",
        });
    }

    private List<QuestDefinition> LoadDefinitions(
        ScourgeQuestDocument document,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var definitions = new List<QuestDefinition>(QuestIds.Length);
        foreach (var id in QuestIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.Quests.TryGetValue(id, out var quest) || quest is null)
            {
                errors.Add($"missing quest template: {id}");
                continue;
            }

            if (quest.Id.ToString() != id)
            {
                errors.Add($"quest catalog key does not match template id: {id} / {quest.Id}");
                continue;
            }

            NormalizeUnsupportedConditionExtensions(id, quest);
            definitions.Add(new QuestDefinition(id, quest));
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "[Scourge] quest catalog is incomplete or inconsistent:\n" + string.Join("\n", errors));
        }

        return definitions;
    }

    private static void NormalizeUnsupportedConditionExtensions(string questId, Quest quest)
    {
        var extensionData = quest.Conditions?.ExtensionData;
        if (extensionData is null)
        {
            return;
        }

        var autoStartKey = extensionData.Keys.FirstOrDefault(static key =>
            string.Equals(key, "AutoStart", StringComparison.OrdinalIgnoreCase));
        if (autoStartKey is null)
        {
            return;
        }

        var autoStart = extensionData[autoStartKey];
        extensionData.Remove(autoStartKey);
        if (!IsEmptyUnsupportedConditionValue(autoStart))
        {
            throw new InvalidDataException(
                $"[Scourge] quest {questId} contains unsupported non-empty conditions.AutoStart; "
                + "refusing to lose its semantics.");
        }
    }

    private static bool IsEmptyUnsupportedConditionValue(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        if (value is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var enumerator = enumerable.GetEnumerator();
            try
            {
                return !enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        return false;
    }

    private void RegisterImages(
        IReadOnlyList<QuestDefinition> definitions,
        string modPath,
        CancellationToken cancellationToken)
    {
        var registeredRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = definition.Quest.Image;
            if (string.IsNullOrWhiteSpace(image))
            {
                throw new InvalidDataException($"[Scourge] quest {definition.Id} has no image path.");
            }

            var route = StripExtension(image);
            if (!registeredRoutes.Add(route))
            {
                continue;
            }

            var relativePath = image.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relativePath.StartsWith("files" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath[("files".Length + 1)..];
            }

            var localPath = Path.Combine(modPath, "images", relativePath);
            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException(
                    $"[Scourge] quest icon is missing for route '{image}'; reinstall the complete mod package.",
                    localPath);
            }

            // ImageRouter removes the request extension before lookup. Keep
            // the original /files/quest/icon route and map it to our packaged
            // image file, regardless of whether the client asks for .jpg.
            imageRouter.AddRoute(route, localPath);
        }
    }

    private List<string> Preflight(
        IReadOnlyList<QuestDefinition> definitions,
        IReadOnlyDictionary<string, Dictionary<string, string>> locales)
    {
        var errors = new List<string>();
        var definitionIds = QuestIds
            .Select(static id => new MongoId(id))
            .ToHashSet();

        foreach (var definition in definitions)
        {
            // A quest supplied by another mod is outside this catalog's
            // ownership. It is deliberately skipped later and therefore
            // should not make this mod fail preflight on its own references.
            if (templateTable.Quests.ContainsKey(definition.Quest.Id))
            {
                continue;
            }

            var quest = definition.Quest;
            if (!tradersTable.ContainsKey(quest.TraderId))
            {
                errors.Add($"{definition.Id} trader does not exist: {quest.TraderId}");
            }

            CheckConditions(definition.Id, quest.Conditions.AvailableForStart, definitionIds, errors);
            CheckConditions(definition.Id, quest.Conditions.AvailableForFinish, definitionIds, errors);
            CheckLocales(definition.Id, quest, locales, errors);
            CheckRewards(definition.Id, quest, errors);
        }

        return errors;
    }

    private void CheckConditions(
        string questId,
        IEnumerable<QuestCondition>? conditions,
        IReadOnlySet<MongoId> definitionIds,
        ICollection<string> errors)
    {
        foreach (var condition in conditions ?? [])
        {
            var targets = TargetValues(condition.Target);
            if (condition.ConditionType is "FindItem" or "HandoverItem")
            {
                foreach (var target in targets)
                {
                    if (!MongoId.IsValidMongoId(target)
                        || !templateTable.Items.ContainsKey(new MongoId(target)))
                    {
                        errors.Add($"{questId} item condition references an unavailable item: {target}");
                    }
                }
            }
            else if (condition.ConditionType == "Quest")
            {
                foreach (var target in targets)
                {
                    if (!MongoId.IsValidMongoId(target))
                    {
                        errors.Add($"{questId} has an invalid prerequisite quest id: {target}");
                    }
                    else if (!definitionIds.Contains(new MongoId(target))
                        && !templateTable.Quests.ContainsKey(new MongoId(target)))
                    {
                        errors.Add($"{questId} prerequisite quest is not registered: {target}");
                    }
                }
            }

            if (condition.Counter is null)
            {
                continue;
            }

            foreach (var nested in condition.Counter.Conditions ?? [])
            {
                if (nested.ConditionType is not ("FindItem" or "HandoverItem"))
                {
                    continue;
                }

                foreach (var target in TargetValues(nested.Target))
                {
                    if (!MongoId.IsValidMongoId(target)
                        || !templateTable.Items.ContainsKey(new MongoId(target)))
                    {
                        errors.Add($"{questId} counter item condition references an unavailable item: {target}");
                    }
                }
            }
        }
    }

    private void CheckRewards(string questId, Quest quest, ICollection<string> errors)
    {
        if (quest.Rewards is not { } rewardMap)
        {
            return;
        }

        foreach (var rewards in rewardMap.Values)
        {
            foreach (var reward in rewards)
            {
                foreach (var item in reward.Items ?? [])
                {
                    if (item.Template.IsEmpty || !templateTable.Items.ContainsKey(item.Template))
                    {
                        errors.Add($"{questId} reward item template is not registered: {item.Template}");
                    }
                }

                if (reward.Type == RewardType.TraderStanding
                    && (!MongoId.IsValidMongoId(reward.Target ?? string.Empty)
                        || !tradersTable.ContainsKey(new MongoId(reward.Target!))))
                {
                    errors.Add($"{questId} reward trader does not exist: {reward.Target}");
                }
            }
        }
    }

    private static void CheckLocales(
        string questId,
        Quest quest,
        IReadOnlyDictionary<string, Dictionary<string, string>> locales,
        ICollection<string> errors)
    {
        var requiredKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{questId} name",
            $"{questId} description",
            $"{questId} successMessageText",
            $"{questId} failMessageText",
            $"{questId} acceptPlayerMessage",
            $"{questId} declinePlayerMessage",
            $"{questId} completePlayerMessage",
        };
        var requiredNonEmptyKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{questId} name",
            $"{questId} description",
        };

        // Level and prerequisite gates use the client's native gate display;
        // only visible finish objectives and their counter children need
        // custom locale entries.
        foreach (var condition in quest.Conditions.AvailableForFinish ?? [])
        {
            AddConditionLocaleKeys(condition, requiredKeys);
        }

        foreach (var (language, entries) in locales)
        {
            foreach (var key in requiredKeys)
            {
                if (!entries.TryGetValue(key, out var value))
                {
                    errors.Add($"{questId} locale {language} is missing key: {key}");
                }
                else if (requiredNonEmptyKeys.Contains(key) && string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"{questId} locale {language} has blank key: {key}");
                }
            }
        }
    }

    private static void AddConditionLocaleKeys(QuestCondition condition, ISet<string> keys)
    {
        keys.Add(condition.Id.ToString());
    }

    private static Dictionary<string, Dictionary<string, string>> BuildLocales(
        ScourgeQuestDocument document,
        IEnumerable<string> installedLanguages)
    {
        var english = FindLocale(document.Locales, "en")
            ?? throw new InvalidDataException("[Scourge] quest catalog has no English locale fallback.");

        var languages = installedLanguages
            .Where(static language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
        {
            languages.Add("en");
        }

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in languages)
        {
            var source = FindLocale(document.Locales, language);
            var entries = new Dictionary<string, string>(english, StringComparer.Ordinal);
            if (source is not null)
            {
                foreach (var (key, value) in source)
                {
                    // Keep English for missing/blank translations when an
                    // English value exists. If English is also blank, retain
                    // the source blank intentionally rather than inventing
                    // text.
                    if (string.IsNullOrWhiteSpace(value)
                        && entries.TryGetValue(key, out var englishValue)
                        && !string.IsNullOrWhiteSpace(englishValue))
                    {
                        continue;
                    }

                    entries[key] = value;
                }
            }

            result[language] = entries;
        }

        return result;
    }

    private static Dictionary<string, string>? FindLocale(
        IReadOnlyDictionary<string, Dictionary<string, string>> locales,
        string language)
    {
        foreach (var (key, entries) in locales)
        {
            if (string.Equals(key, language, StringComparison.OrdinalIgnoreCase))
            {
                return entries;
            }
        }

        return null;
    }

    private static IEnumerable<string> TargetValues(ListOrT<string>? target)
    {
        if (target is null)
        {
            yield break;
        }

        if (target.IsItem && target.Item is not null)
        {
            yield return target.Item;
            yield break;
        }

        foreach (var value in target.List ?? [])
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static string StripExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension)
            ? path
            : path[..^extension.Length];
    }

    private sealed record QuestDefinition(string Id, Quest Quest);

    private sealed class ScourgeQuestDocument
    {
        [JsonPropertyName("quests")]
        public Dictionary<string, Quest> Quests { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("locales")]
        public Dictionary<string, Dictionary<string, string>> Locales { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
