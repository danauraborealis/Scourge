using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Manimal.Scourge.Server;

/// <summary>
/// Compatibility for the first 4.1.6 runtime build, whose QuestConfig does
/// not yet expose StartedWithQuests and whose QuestController therefore cannot
/// start Doxed through the native storyline map. Newer runtimes keep the
/// native configuration path and never enable this patch.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class ScourgeQuestStartPatch : AbstractPatch
{
    private static readonly MongoId SideHustleQuestId = new("6ac4c42976842fabb501d5b8");
    private static readonly MongoId DoxedQuestId = new("6ac4c726a3ea5ab42a074d3a");

    private static ICloner? _cloner;
    private static TemplateTable? _templateTable;

    [ThreadStatic]
    private static int _nestedAcceptDepth;

    private bool _enabled;

    public ScourgeQuestStartPatch(ICloner cloner, TemplateTable templateTable)
        : base($"{ModIdentity.Guid}.quest-start-compat")
    {
        _cloner = cloner;
        _templateTable = templateTable;
    }

    internal void EnableCompatibility()
    {
        if (_enabled)
        {
            return;
        }

        Enable();
        _enabled = true;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(QuestController).GetMethod(
                   nameof(QuestController.AcceptQuest),
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   types: [typeof(PmcData), typeof(AcceptQuestRequestData), typeof(MongoId)],
                   modifiers: null)
               ?? throw new MissingMethodException(
                   "[Scourge] SPT QuestController.AcceptQuest(PmcData, AcceptQuestRequestData, MongoId) is unavailable.");
    }

    [PatchPostfix]
    private static void Postfix(
        QuestController __instance,
        PmcData pmcData,
        AcceptQuestRequestData acceptedQuest,
        MongoId sessionID,
        ref ItemEventRouterResponse __result)
    {
        if (_nestedAcceptDepth != 0 || acceptedQuest.QuestId != SideHustleQuestId)
        {
            return;
        }

        // AcceptQuest can still return a response after a rejected request.
        // Never turn that failed parent request into a successful Doxed start.
        if (__result.Warnings is { Count: > 0 })
        {
            return;
        }

        var sideHustleStatus = pmcData.Quests?.FirstOrDefault(quest => quest.QId == SideHustleQuestId);
        if (sideHustleStatus?.Status is not (QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish))
        {
            return;
        }

        var doxedStatus = pmcData.Quests?.FirstOrDefault(quest => quest.QId == DoxedQuestId);
        if (doxedStatus?.Status is QuestStatusEnum.Started
            or QuestStatusEnum.AvailableForFinish
            or QuestStatusEnum.Success)
        {
            // A replayed Side Hustle accept must not reset or duplicate Doxed.
            EnsureDoxedClientState(__result, pmcData, sessionID);
            return;
        }

        _nestedAcceptDepth++;
        try
        {
            var doxedResponse = __instance.AcceptQuest(
                pmcData,
                new AcceptQuestRequestData { QuestId = DoxedQuestId },
                sessionID);

            // The old QuestController mutates pmcData.Quests for this nested
            // accept, but does not emit the corresponding QuestStatus.  The
            // outer response can therefore still carry Doxed as an available
            // quest template.  Make the client payload agree with the saved
            // profile before merging the nested response.
            EnsureDoxedClientState(doxedResponse, pmcData, sessionID);
            MergeResponse(__result, doxedResponse, sessionID);
            EnsureDoxedClientState(__result, pmcData, sessionID);
        }
        finally
        {
            _nestedAcceptDepth--;
        }
    }

    private static void EnsureDoxedClientState(
        ItemEventRouterResponse response,
        PmcData pmcData,
        MongoId sessionID)
    {
        var doxedStatus = pmcData.Quests?.FirstOrDefault(quest => quest.QId == DoxedQuestId);
        if (doxedStatus is null)
        {
            return;
        }

        if (!response.ProfileChanges.TryGetValue(sessionID, out var profileChange))
        {
            profileChange = new ProfileChange();
            response.ProfileChanges[sessionID] = profileChange;
        }

        profileChange.QuestsStatus ??= [];
        for (var index = profileChange.QuestsStatus.Count - 1; index >= 0; index--)
        {
            if (profileChange.QuestsStatus[index].QId == DoxedQuestId)
            {
                profileChange.QuestsStatus.RemoveAt(index);
            }
        }

        profileChange.QuestsStatus.Add(doxedStatus);

        // Doxed was hidden from the initial quest list.  Mirror native
        // StartedWithQuests behavior by returning one cloned started template
        // as well as the profile status.  Never mutate TemplateTable.Quests.
        profileChange.Quests ??= [];
        for (var index = profileChange.Quests.Count - 1; index >= 0; index--)
        {
            if (profileChange.Quests[index].Id == DoxedQuestId)
            {
                profileChange.Quests.RemoveAt(index);
            }
        }

        if (_templateTable?.Quests.TryGetValue(DoxedQuestId, out var sourceTemplate) != true)
        {
            return;
        }

        var doxedTemplate = _cloner?.Clone(sourceTemplate);
        if (doxedTemplate is null)
        {
            return;
        }

        doxedTemplate.Status = (int)doxedStatus.Status;
        doxedTemplate.SptStatus = doxedStatus.Status;
        profileChange.Quests.Add(doxedTemplate);
    }

    private static void MergeResponse(
        ItemEventRouterResponse primary,
        ItemEventRouterResponse secondary,
        MongoId sessionID)
    {
        // QuestController obtains both responses from EventOutputHolder, so
        // the normal path is already one shared object. Keep this defensive
        // merge for runtimes that return a distinct response instance.
        if (ReferenceEquals(primary, secondary))
        {
            return;
        }

        if (secondary.Warnings is { Count: > 0 })
        {
            primary.Warnings ??= [];
            primary.Warnings.AddRange(secondary.Warnings);
        }

        if (!secondary.ProfileChanges.TryGetValue(sessionID, out var secondaryChange))
        {
            return;
        }

        if (!primary.ProfileChanges.TryGetValue(sessionID, out var primaryChange))
        {
            primary.ProfileChanges[sessionID] = secondaryChange;
            return;
        }

        primaryChange.Quests ??= [];
        if (secondaryChange.Quests is { Count: > 0 })
        {
            primaryChange.Quests.AddRange(secondaryChange.Quests);
        }

        primaryChange.QuestsStatus ??= [];
        if (secondaryChange.QuestsStatus is { Count: > 0 })
        {
            primaryChange.QuestsStatus.AddRange(secondaryChange.QuestsStatus);
        }

        if (secondaryChange.Items is not null)
        {
            primaryChange.Items ??= new ItemChanges { DeletedItems = [] };
            primaryChange.Items.NewItems ??= [];
            primaryChange.Items.ChangedItems ??= [];
            if (secondaryChange.Items.NewItems is { Count: > 0 })
            {
                primaryChange.Items.NewItems.AddRange(secondaryChange.Items.NewItems);
            }

            if (secondaryChange.Items.ChangedItems is { Count: > 0 })
            {
                primaryChange.Items.ChangedItems.AddRange(secondaryChange.Items.ChangedItems);
            }

            if (secondaryChange.Items.DeletedItems is { Count: > 0 })
            {
                primaryChange.Items.DeletedItems.AddRange(secondaryChange.Items.DeletedItems);
            }
        }
    }
}
