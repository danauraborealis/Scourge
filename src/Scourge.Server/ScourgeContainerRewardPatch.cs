using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Manimal.Scourge.Server;

internal sealed record ScourgeGuaranteedRange(MongoId TemplateId, int Min, int Max);

// SPT 4.1.6's installed RewardDetails model preserves unknown guaranteedItems
// JSON, but its LootGenerator only consumes RewardCount/RewardTplPool. Keep the
// compatibility patch narrowly keyed to the exact RewardDetails instances that
// Scourge inserted into InventoryConfig.RandomLootContainers.
[Injectable(InjectionType.Singleton)]
public sealed class ScourgeContainerRewardPatch(TemplateTable templateTable)
    : AbstractPatch($"{ModIdentity.Guid}.container-rewards")
{
    internal const int MaxGuaranteedAmount = 1_000_000;

    private static readonly Dictionary<RewardDetails, IReadOnlyList<RegisteredReward>> RegisteredRewards =
        new(ReferenceEqualityComparer.Instance);

    private bool _registrationClosed;
    private bool _enabled;

    internal void Register(RewardDetails rewardDetails, IReadOnlyList<ScourgeGuaranteedRange> guaranteedRanges)
    {
        ArgumentNullException.ThrowIfNull(rewardDetails);
        ArgumentNullException.ThrowIfNull(guaranteedRanges);

        if (_registrationClosed)
        {
            throw new InvalidOperationException("[Scourge] container reward registration is already closed.");
        }

        if (guaranteedRanges.Count == 0)
        {
            throw new InvalidDataException(
                $"[Scourge] container '{rewardDetails.Type}' has no guaranteed rewards to register.");
        }

        var registered = new List<RegisteredReward>(guaranteedRanges.Count);
        foreach (var range in guaranteedRanges)
        {
            if (range.Min < 1
                || range.Max < range.Min
                || range.Max > MaxGuaranteedAmount)
            {
                throw new InvalidDataException(
                    $"[Scourge] guaranteed reward '{range.TemplateId}' has invalid range {range.Min}..{range.Max}.");
            }

            if (!templateTable.Items.TryGetValue(range.TemplateId, out var template))
            {
                throw new InvalidDataException(
                    $"[Scourge] guaranteed reward '{range.TemplateId}' is missing from the item table.");
            }

            var stackMaxSize = template.Properties?.StackMaxSize;
            if (stackMaxSize is not > 0)
            {
                throw new InvalidDataException(
                    $"[Scourge] guaranteed reward '{range.TemplateId}' has no positive StackMaxSize.");
            }

            registered.Add(new RegisteredReward(
                range.TemplateId,
                range.Min,
                range.Max,
                stackMaxSize.Value));
        }

        if (!RegisteredRewards.TryAdd(rewardDetails, registered))
        {
            throw new InvalidOperationException(
                $"[Scourge] duplicate guaranteed reward registration for container '{rewardDetails.Type}'.");
        }
    }

    internal void FinalizeRegistration()
    {
        if (_registrationClosed)
        {
            return;
        }

        _registrationClosed = true;
        if (RegisteredRewards.Count == 0 || _enabled)
        {
            return;
        }

        Enable();
        _enabled = true;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LootGenerator).GetMethod(
                   nameof(LootGenerator.GetRandomLootContainerLoot),
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   types: [typeof(RewardDetails)],
                   modifiers: null)
               ?? throw new MissingMethodException(
                   "[Scourge] SPT LootGenerator.GetRandomLootContainerLoot(RewardDetails) is unavailable.");
    }

    [PatchPostfix]
    private static void Postfix(
        RewardDetails rewardContainerDetails,
        ref List<List<Item>> __result)
    {
        if (!RegisteredRewards.TryGetValue(rewardContainerDetails, out var guaranteedRewards))
        {
            return;
        }

        foreach (var guaranteedReward in guaranteedRewards)
        {
            var remaining = Random.Shared.Next(
                guaranteedReward.Min,
                checked(guaranteedReward.Max + 1));

            while (remaining > 0)
            {
                var stackSize = Math.Min(remaining, guaranteedReward.StackMaxSize);
                __result.Add(
                [
                    new Item
                    {
                        Id = new MongoId(),
                        Template = guaranteedReward.TemplateId,
                        Upd = new Upd { StackObjectsCount = stackSize },
                    },
                ]);
                remaining -= stackSize;
            }
        }
    }

    private sealed record RegisteredReward(
        MongoId TemplateId,
        int Min,
        int Max,
        int StackMaxSize);
}
