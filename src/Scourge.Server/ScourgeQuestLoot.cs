using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace Manimal.Scourge.Server;

/// <summary>
/// Adds the original Scourge quest-item and authorized precursor-container
/// locations to the in-memory location table. Other event case and phone
/// world placements are intentionally not part of this registration.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 21)]
public sealed class ScourgeQuestLoot(
    LocationTable locationTable,
    TemplateTable templateTable,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    ICloner cloner,
    ISptLogger<ScourgeQuestLoot> logger) : IOnLoad
{
    private const string QuestLootDataFile = "db/questLoot/questLoot.json";

    private static readonly MongoId FlashDriveId = new("6a919148d1f99fb50b0f3113");
    private static readonly MongoId SsdId = new("6ac64a82d614fb7b2f0516a1");
    private static readonly MongoId PrecursorId = new("6ac4d528d8aa0d51df01995c");
    private static readonly MongoId SuitcaseId = new("6ac4e0e576842fabb501d5d0");
    private static readonly MongoId PackageId = new("6ac4eccad8aa0d51df019974");
    private static readonly HashSet<MongoId> ExpectedQuestItemIds = [FlashDriveId, SsdId];
    private static readonly HashSet<MongoId> ExpectedContainerRootIds = [SuitcaseId, PackageId];
    private static readonly HashSet<MongoId> ExpectedRootIds = [FlashDriveId, SsdId, SuitcaseId, PackageId];

    private static readonly Dictionary<MongoId, int> ExpectedItemCounts = new()
    {
        [FlashDriveId] = 1,
        [SsdId] = 1,
        [SuitcaseId] = 59,
        [PackageId] = 10,
    };

    private static readonly HashSet<string> ExpectedMaps =
    [
        "laboratory",
        "sandbox",
        "sandbox_high",
        "tarkovstreets",
    ];

    private static readonly Dictionary<string, int> ExpectedPointCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["laboratory"] = 3,
        ["sandbox"] = 1,
        ["sandbox_high"] = 1,
        ["tarkovstreets"] = 2,
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var dataPath = Path.Combine(modPath, QuestLootDataFile.Replace('/', Path.DirectorySeparatorChar));
        var pointsByMap = jsonUtil.DeserializeFromFile<Dictionary<string, Spawnpoint[]>>(dataPath)
            ?? throw new InvalidDataException($"[Scourge] quest-loot data is missing or invalid: {dataPath}");

        ValidateQuestItemDefinitions();
        ValidatePoints(pointsByMap);

        foreach (var (mapName, points) in pointsByMap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var location = locationTable.GetLocation(mapName)
                ?? throw new InvalidDataException($"[Scourge] quest-loot map '{mapName}' is not available in LocationTable.");
            if (location.LooseLoot is null)
            {
                throw new InvalidDataException($"[Scourge] quest-loot map '{mapName}' has no LooseLoot table.");
            }

            location.LooseLoot.AddTransformer(looseLoot =>
            {
                if (looseLoot is null)
                {
                    throw new InvalidDataException($"[Scourge] quest-loot map '{mapName}' resolved to null LooseLoot data.");
                }

                var forcedPoints = (looseLoot.SpawnpointsForced ?? []).ToList();
                foreach (var point in points)
                {
                    var rootTemplateId = GetRootTemplateId(point);
                    if (forcedPoints.Any(existing => HasExistingPoint(existing, point, rootTemplateId)))
                    {
                        // Another mod (or a future SPT data set) already owns
                        // this quest point. Preserve it. Quest pickups use
                        // their root tpl as the ownership key; container
                        // alternatives use the full point identity so two
                        // alternatives sharing one Template.Id survive.
                        continue;
                    }

                    forcedPoints.Add(
                        cloner.Clone(point)
                        ?? throw new InvalidDataException($"[Scourge] quest-loot point '{point.LocationId ?? "<unknown>"}' could not be cloned."));
                }

                looseLoot.SpawnpointsForced = forcedPoints;
                return looseLoot;
            });
        }

        logger.Info(
            $"[Scourge] prepared quest-item forced loot for {pointsByMap.Count} maps "
            + $"({pointsByMap.Values.Sum(points => points.Length)} source points; existing quest-item points are preserved).");
        return Task.CompletedTask;
    }

    private void ValidateQuestItemDefinitions()
    {
        foreach (var questItemId in ExpectedQuestItemIds)
        {
            if (!templateTable.Items.TryGetValue(questItemId, out var item) || item?.Properties is null)
            {
                throw new InvalidDataException(
                    $"[Scourge] quest-loot item '{questItemId}' is missing from TemplateTable; content must load first.");
            }

            if (item.Properties.QuestItem != true)
            {
                throw new InvalidDataException($"[Scourge] quest-loot item '{questItemId}' is not marked QuestItem.");
            }
        }
    }

    private void ValidatePoints(Dictionary<string, Spawnpoint[]> pointsByMap)
    {
        if (pointsByMap.Count != ExpectedMaps.Count || pointsByMap.Keys.Any(map => !ExpectedMaps.Contains(map)))
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot data must contain exactly the maps [{string.Join(", ", ExpectedMaps.Order())}].");
        }

        foreach (var (map, mapPoints) in pointsByMap)
        {
            if (!ExpectedPointCounts.TryGetValue(map, out var expectedCount) || mapPoints is null)
            {
                throw new InvalidDataException($"[Scourge] quest-loot map '{map}' has no point list.");
            }

            if (mapPoints.Length != expectedCount)
            {
                throw new InvalidDataException(
                    $"[Scourge] quest-loot map '{map}' must contain exactly {expectedCount} points, found {mapPoints.Length}.");
            }
        }

        var points = pointsByMap.SelectMany(pair => pair.Value.Select(point => (Map: pair.Key, Point: point))).ToArray();
        if (points.Length != 7)
        {
            throw new InvalidDataException($"[Scourge] quest-loot data must contain exactly seven runtime points, found {points.Length}.");
        }

        var rootTemplateIds = points.Select(point => GetRootTemplateId(point.Point)).ToHashSet();
        if (!rootTemplateIds.SetEquals(ExpectedRootIds))
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot data must contain exactly the expected root templates [{string.Join(", ", ExpectedRootIds)}].");
        }

        foreach (var (map, point) in points)
        {
            if (point.Template is null)
            {
                throw new InvalidDataException($"[Scourge] quest-loot point on '{map}' has no template.");
            }

            ValidatePoint(map, point);
        }

        RequireRootCount(points, "laboratory", FlashDriveId, 1);
        RequireRootCount(points, "laboratory", SuitcaseId, 2);
        RequireRootCount(points, "sandbox", SsdId, 1);
        RequireRootCount(points, "sandbox_high", SsdId, 1);
        RequireRootCount(points, "tarkovstreets", PackageId, 2);

        EnsureSharedTemplateId(points, "laboratory", SuitcaseId, "item_container_event_scourge_suitcase [a0cb9f37-751c-4f4e-bf18-054bb3aee6e0]");
        EnsureSharedTemplateId(points, "tarkovstreets", PackageId, "item_container_event_scourge_box [55863891-6522-4508-a5cd-d307db4ff370]");
    }

    private void ValidatePoint(string map, Spawnpoint point)
    {
        var template = point.Template
            ?? throw new InvalidDataException($"[Scourge] quest-loot point on '{map}' has no template.");
        var items = template.Items?.ToArray()
            ?? throw new InvalidDataException($"[Scourge] quest-loot point '{template.Id}' on '{map}' has no item tree.");
        var rootItemId = TryReadMongoId(template.Root, out var parsedRootItemId)
            ? parsedRootItemId
            : throw new InvalidDataException($"[Scourge] quest-loot point '{template.Id}' on '{map}' has an invalid Root item id.");
        var rootItem = items.SingleOrDefault(item => item.Id == rootItemId)
            ?? throw new InvalidDataException($"[Scourge] quest-loot point '{template.Id}' on '{map}' has no item matching Root '{template.Root}'.");
        var rootTemplateId = rootItem.Template;

        if (!ExpectedRootIds.Contains(rootTemplateId))
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot point '{template.Id}' on '{map}' has unexpected root template '{rootTemplateId}'.");
        }

        if (!ExpectedItemCounts.TryGetValue(rootTemplateId, out var expectedItemCount) || items.Length != expectedItemCount)
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot point '{template.Id}' on '{map}' has {items.Length} items; expected {expectedItemCount}.");
        }

        var expectedProbability = ExpectedQuestItemIds.Contains(rootTemplateId) ? 1d : 0.5d;
        if (point.Probability is null
            || point.Probability < 0
            || point.Probability > 1
            || point.Probability.Value != expectedProbability)
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot point '{template.Id}' on '{map}' has probability {point.Probability}; expected {expectedProbability}.");
        }

        if (string.IsNullOrWhiteSpace(template.Id)
            || template.IsAlwaysSpawn == true
            || template.IsGroupPosition == true
            || template.IsContainer == true)
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot point '{template.Id}' on '{map}' does not match the original forced-point shape.");
        }

        if (point.ItemDistribution is not null && point.ItemDistribution.Any())
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot point '{template.Id}' on '{map}' must not define an item distribution.");
        }

        var itemsById = new Dictionary<MongoId, SptLootItem>();
        foreach (var item in items)
        {
            if (item.Id.IsEmpty || !itemsById.TryAdd(item.Id, item))
            {
                throw new InvalidDataException(
                    $"[Scourge] quest-loot point '{template.Id}' on '{map}' contains a duplicate or empty item id.");
            }

            if (!templateTable.Items.ContainsKey(item.Template))
            {
                throw new InvalidDataException(
                    $"[Scourge] quest-loot point '{template.Id}' on '{map}' references missing item template '{item.Template}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(rootItem.ParentId))
        {
            throw new InvalidDataException($"[Scourge] root item '{rootItem.Id}' on '{map}' must not have a parent.");
        }

        foreach (var item in items)
        {
            if (item.Id == rootItemId)
            {
                continue;
            }

            if (!TryReadMongoId(item.ParentId, out var parentId) || !itemsById.ContainsKey(parentId))
            {
                throw new InvalidDataException(
                    $"[Scourge] item '{item.Id}' in point '{template.Id}' on '{map}' has an invalid parent '{item.ParentId}'.");
            }

            var ancestry = new HashSet<MongoId>();
            var current = item;
            while (current.Id != rootItemId)
            {
                if (!ancestry.Add(current.Id)
                    || !TryReadMongoId(current.ParentId, out var ancestorId)
                    || !itemsById.TryGetValue(ancestorId, out current))
                {
                    throw new InvalidDataException(
                        $"[Scourge] item '{item.Id}' in point '{template.Id}' on '{map}' has an invalid or cyclic ancestry.");
                }
            }
        }

        if (ExpectedContainerRootIds.Contains(rootTemplateId)
            && items.Count(item => item.Template == PrecursorId) != 1)
        {
            throw new InvalidDataException(
                $"[Scourge] container point '{template.Id}' on '{map}' must contain exactly one nested precursor.");
        }
    }

    private static void RequireRootCount(
        IEnumerable<(string Map, Spawnpoint Point)> points,
        string map,
        MongoId rootTemplateId,
        int expectedCount)
    {
        var actualCount = points.Count(point =>
            point.Map.Equals(map, StringComparison.OrdinalIgnoreCase)
            && GetRootTemplateId(point.Point) == rootTemplateId);
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException(
                $"[Scourge] quest-loot map '{map}' must contain {expectedCount} point(s) rooted at '{rootTemplateId}', found {actualCount}.");
        }
    }

    private static void EnsureSharedTemplateId(
        IEnumerable<(string Map, Spawnpoint Point)> points,
        string map,
        MongoId rootTemplateId,
        string expectedTemplateId)
    {
        var templateIds = points
            .Where(point => point.Map.Equals(map, StringComparison.OrdinalIgnoreCase) && GetRootTemplateId(point.Point) == rootTemplateId)
            .Select(point => point.Point.Template?.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (templateIds.Length != 1 || !string.Equals(templateIds[0], expectedTemplateId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"[Scourge] alternatives for '{rootTemplateId}' on '{map}' must share template id '{expectedTemplateId}'.");
        }
    }

    private static bool TryReadMongoId(string? value, out MongoId id)
    {
        try
        {
            id = new MongoId(value);
            return !id.IsEmpty;
        }
        catch (Exception)
        {
            id = default;
            return false;
        }
    }

    private static MongoId GetRootTemplateId(Spawnpoint point)
    {
        var item = point.Template?.Items?.FirstOrDefault()
            ?? throw new InvalidDataException("[Scourge] quest-loot point has no root item.");
        return item.Template;
    }

    private static bool HasExistingPoint(Spawnpoint existing, Spawnpoint desired, MongoId desiredRootTemplateId)
    {
        if (ExpectedQuestItemIds.Contains(desiredRootTemplateId))
        {
            return existing.Template?.Items?.FirstOrDefault()?.Template == desiredRootTemplateId;
        }

        var existingTemplate = existing.Template;
        var desiredTemplate = desired.Template;
        var existingPosition = existingTemplate?.Position;
        var desiredPosition = desiredTemplate?.Position;
        return existingTemplate is not null
            && desiredTemplate is not null
            && existingPosition is { } existingVector
            && desiredPosition is { } desiredVector
            && string.Equals(existing.LocationId, desired.LocationId, StringComparison.Ordinal)
            && string.Equals(existingTemplate.Id, desiredTemplate.Id, StringComparison.Ordinal)
            && existingVector.X == desiredVector.X
            && existingVector.Y == desiredVector.Y
            && existingVector.Z == desiredVector.Z;
    }
}
