using System.Reflection;
using System.Text.Json;
using IoPath = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Scourge.Server;

[Injectable(InjectionType.Singleton)]
public sealed class ScourgeContent(
    JsonUtil jsonUtil,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ItemBaseClassService itemBaseClassService,
    InventoryConfig inventoryConfig,
    ScourgeContainerRewardPatch containerRewardPatch,
    ISptLogger<ScourgeContent> logger)
{
    private static readonly MongoId[] RequiredItemIds =
    [
        new("6ac4cfbc99bdd7779608792f"),
        new("6ac4d528d8aa0d51df01995c"),
        new("6ac4e0e576842fabb501d5d0"),
        new("6ac4eccad8aa0d51df019974"),
        new("6ac64a82d614fb7b2f0516a1"),
    ];

    private bool _loaded;

    public int Load(Assembly assembly)
    {
        if (_loaded)
        {
            return 0;
        }

        var contentRoot = IoPath.Combine(
            IoPath.GetDirectoryName(assembly.Location)
                ?? throw new InvalidOperationException("[Scourge] server assembly location is unavailable."),
            "db",
            "content");

        var items = ReadRequired<Dictionary<MongoId, TemplateItem>>(contentRoot, "items.json");
        ValidateItemSet(items);

        var addedItems = new List<MongoId>();
        foreach (var (itemId, item) in items)
        {
            if (item.Id != itemId)
            {
                throw new InvalidDataException(
                    $"[Scourge] content item key '{itemId}' does not match template _id '{item.Id}'.");
            }

            if (templateTable.Items.ContainsKey(itemId))
            {
                logger.Info($"[Scourge] preserving installed item template '{itemId}'.");
                continue;
            }

            if (!templateTable.Items.ContainsKey(item.Parent))
            {
                throw new InvalidDataException(
                    $"[Scourge] content item '{itemId}' references missing parent '{item.Parent}'.");
            }

            if (!templateTable.Items.TryAdd(itemId, item))
            {
                throw new InvalidOperationException($"[Scourge] failed to add item template '{itemId}'.");
            }

            addedItems.Add(itemId);
        }

        var containerRewardsPath = IoPath.Combine(contentRoot, "containerRewards.json");
        var containerRewards = ReadRequired<Dictionary<MongoId, RewardDetails>>(contentRoot, "containerRewards.json");
        var guaranteedRewards = ReadGuaranteedRewards(containerRewardsPath);
        ValidateContainerRewards(containerRewards, guaranteedRewards);
        foreach (var (containerId, rewardDetails) in containerRewards)
        {
            if (!inventoryConfig.RandomLootContainers.TryAdd(containerId, rewardDetails))
            {
                logger.Info($"[Scourge] preserving installed random loot container definition '{containerId}'.");
                continue;
            }

            if (guaranteedRewards.TryGetValue(containerId, out var containerGuaranteedRewards))
            {
                containerRewardPatch.Register(rewardDetails, containerGuaranteedRewards);
            }
        }
        containerRewardPatch.FinalizeRegistration();

        foreach (var handbookItem in ReadRequired<List<HandbookItem>>(contentRoot, "handbook.json"))
        {
            if (!templateTable.Items.ContainsKey(handbookItem.Id))
            {
                throw new InvalidDataException(
                    $"[Scourge] handbook entry '{handbookItem.Id}' has no corresponding item template.");
            }

            if (templateTable.Handbook.Items.All(existing => existing.Id != handbookItem.Id))
            {
                templateTable.Handbook.Items.Add(handbookItem);
            }
        }

        foreach (var (itemId, price) in ReadRequired<Dictionary<MongoId, double>>(contentRoot, "prices.json"))
        {
            if (!templateTable.Prices.ContainsKey(itemId))
            {
                templateTable.Prices[itemId] = price;
            }
        }

        RegisterLocales(ReadRequired<Dictionary<string, Dictionary<string, string>>>(contentRoot, "locales.json"));

        foreach (var itemId in addedItems)
        {
            itemBaseClassService.AddItemToCache(itemId);
        }

        _loaded = true;
        logger.Info(
            $"[Scourge] loaded {items.Count} event item templates ({addedItems.Count} added), "
            + $"{templateTable.Handbook.Items.Count(handbook => RequiredItemIds.Contains(handbook.Id))} handbook entries, "
            + $"{containerRewards.Count} random loot container definitions, and localized names before MoreBots load.");
        return addedItems.Count;
    }

    private void ValidateContainerRewards(
        Dictionary<MongoId, RewardDetails> containerRewards,
        Dictionary<MongoId, List<ScourgeGuaranteedRange>> guaranteedRewards)
    {
        foreach (var (containerId, rewardDetails) in containerRewards)
        {
            if (!templateTable.Items.ContainsKey(containerId))
            {
                throw new InvalidDataException(
                    $"[Scourge] random loot container '{containerId}' has no corresponding item template.");
            }

            ValidateRewardTemplates(containerId, rewardDetails.RewardTplPool?.Keys);
            if (!guaranteedRewards.TryGetValue(containerId, out var containerGuaranteedRewards)
                || containerGuaranteedRewards.Count == 0)
            {
                throw new InvalidDataException(
                    $"[Scourge] random loot container '{containerId}' has no guaranteed rewards.");
            }

            ValidateRewardTemplates(containerId, containerGuaranteedRewards.Select(reward => reward.TemplateId));
        }
    }

    private static Dictionary<MongoId, List<ScourgeGuaranteedRange>> ReadGuaranteedRewards(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<MongoId, List<ScourgeGuaranteedRange>>();

        foreach (var container in document.RootElement.EnumerateObject())
        {
            if (!container.Value.TryGetProperty("guaranteedItems", out var guaranteedItems)
                || guaranteedItems.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var rewards = new List<ScourgeGuaranteedRange>();
            foreach (var item in guaranteedItems.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object
                    || !item.Value.TryGetProperty("min", out var minElement)
                    || !item.Value.TryGetProperty("max", out var maxElement)
                    || !minElement.TryGetInt32(out var min)
                    || !maxElement.TryGetInt32(out var max)
                    || min < 1
                    || max < min
                    || max > ScourgeContainerRewardPatch.MaxGuaranteedAmount)
                {
                    throw new InvalidDataException(
                        $"[Scourge] guaranteed reward '{item.Name}' in container '{container.Name}' has an invalid positive bounded min/max range.");
                }

                rewards.Add(new ScourgeGuaranteedRange(new MongoId(item.Name), min, max));
            }

            result[new MongoId(container.Name)] = rewards;
        }

        return result;
    }

    private void ValidateRewardTemplates(MongoId containerId, IEnumerable<MongoId>? rewardTemplateIds)
    {
        foreach (var rewardTemplateId in rewardTemplateIds ?? [])
        {
            if (!templateTable.Items.ContainsKey(rewardTemplateId))
            {
                throw new InvalidDataException(
                    $"[Scourge] random loot container '{containerId}' references missing reward item '{rewardTemplateId}'.");
            }
        }
    }

    private void RegisterLocales(Dictionary<string, Dictionary<string, string>> locales)
    {
        if (!locales.TryGetValue("en", out var english) || english.Count == 0)
        {
            throw new InvalidDataException("[Scourge] content locales do not contain an English fallback.");
        }

        foreach (var (localeCode, lazyLocale) in localeTable.Global)
        {
            var targetLocale = locales.TryGetValue(localeCode, out var localized)
                ? localized
                : english;

            lazyLocale.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return localeData;
                }

                foreach (var (key, englishValue) in english)
                {
                    if (!localeData.ContainsKey(key))
                    {
                        localeData[key] = targetLocale.TryGetValue(key, out var localizedValue)
                            ? localizedValue
                            : englishValue;
                    }
                }

                foreach (var (key, value) in targetLocale)
                {
                    if (!localeData.ContainsKey(key))
                    {
                        localeData[key] = value;
                    }
                }

                return localeData;
            });
        }
    }

    private static void ValidateItemSet(Dictionary<MongoId, TemplateItem> items)
    {
        foreach (var requiredId in RequiredItemIds)
        {
            if (!items.ContainsKey(requiredId))
            {
                throw new InvalidDataException($"[Scourge] content is missing required item '{requiredId}'.");
            }
        }
    }

    private T ReadRequired<T>(string contentRoot, string fileName)
    {
        var path = IoPath.Combine(contentRoot, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"[Scourge] required content file is missing: {path}", path);
        }

        return jsonUtil.DeserializeFromFile<T>(path)
            ?? throw new InvalidDataException($"[Scourge] required content file is empty: {path}");
    }
}
