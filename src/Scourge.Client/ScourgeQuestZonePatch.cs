using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using SPT.Reflection.Patching;
using UnityEngine;
using ZLinq;

namespace Manimal.Scourge.Client;

internal static class ScourgeQuestZonePatch
{
    private const string FactoryDayLocation = "factory4_day";
    private const string FactoryNightLocation = "factory4_night";
    private const string FactoryHiddenRouteTriggerId = "Tour_10_transition_from_factory";
    private const int FactoryHiddenRouteExperience = 0;

    private static readonly Vector3 FactoryHiddenRoutePosition =
        new Vector3(-26.5389996f, -4.085f, -42.0130005f);
    private static readonly Vector3 FactoryHiddenRouteColliderCenter =
        new Vector3(-0.4688225f, 1f, -0.7399845f);
    private static readonly Vector3 FactoryHiddenRouteColliderSize =
        new Vector3(6.0120621f, 2f, 3.5223503f);

    internal static void Enable()
    {
        new ScourgeGameStartedPatch().Enable();
    }

    private static void EnsureFactoryHiddenRouteTrigger(GameWorld gameWorld)
    {
        if (!IsFactoryLocation(gameWorld.LocationId)
            || HasExistingExperienceTrigger(FactoryHiddenRouteTriggerId))
        {
            return;
        }

        var triggerObject = new GameObject("ScourgeFactoryHiddenRouteTrigger");
        triggerObject.SetActive(false);
        triggerObject.transform.SetParent(gameWorld.transform, worldPositionStays: false);
        triggerObject.transform.SetPositionAndRotation(
            FactoryHiddenRoutePosition,
            Quaternion.identity);
        triggerObject.transform.localScale = Vector3.one;

        var collider = triggerObject.AddComponent<BoxCollider>();
        collider.center = FactoryHiddenRouteColliderCenter;
        collider.size = FactoryHiddenRouteColliderSize;
        collider.isTrigger = true;

        var experienceTrigger = triggerObject.AddComponent<ExperienceTrigger>();
        experienceTrigger.SetId(FactoryHiddenRouteTriggerId);
        experienceTrigger._experience = FactoryHiddenRouteExperience;

        // Keep both native components inactive while the ID, experience and
        // collider geometry are initialized. Activating the parent lets Unity
        // run ExperienceTrigger.Awake and the normal trigger lifecycle.
        triggerObject.SetActive(true);
    }

    private static bool IsFactoryLocation(string locationId) =>
        string.Equals(locationId, FactoryDayLocation, StringComparison.Ordinal)
        || string.Equals(locationId, FactoryNightLocation, StringComparison.Ordinal);

    private static bool HasExistingExperienceTrigger(string triggerId) =>
        UnityEngine.Object.FindObjectsOfType<ExperienceTrigger>(includeInactive: true)
            .AsValueEnumerable()
            .Any(trigger => trigger != null
                && string.Equals(trigger.Id, triggerId, StringComparison.Ordinal));

    private sealed class ScourgeGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(GameWorld).GetMethod(
                nameof(GameWorld.OnGameStarted),
                BindingFlags.Instance | BindingFlags.Public);

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            if (__instance == null)
            {
                return;
            }

            EnsureFactoryHiddenRouteTrigger(__instance);
        }
    }
}
