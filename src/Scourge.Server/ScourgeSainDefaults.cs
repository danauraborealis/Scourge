using System.Collections;
using System.Globalization;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;

namespace Manimal.Scourge.Server;

/// <summary>
/// Optional SAIN defaults. The server assembly has no compile-time SAIN
/// reference, so an absent or changed SAIN API is a safe no-op.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class ScourgeSainDefaults(ISptLogger<ScourgeSainDefaults> logger)
{
    private static Action<object?>? _generatedPostfix;
    private static Action<object?>? _composeCustomPostfix;
    private bool _attempted;

    public void EnableIfAvailable()
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;
        var sainAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "SAINServerMod", StringComparison.Ordinal));
        if (sainAssembly is null)
        {
            logger.Info("[Scourge] optional SAIN defaults disabled: SAINServerMod is not loaded.");
            return;
        }

        try
        {
            var schema = SainSchema.Resolve(sainAssembly);
            _generatedPostfix = ApplyGenerated;
            _composeCustomPostfix = ApplyCustom;
            var enabled = false;
            try
            {
                new GeneratePatch(schema.Generate).Enable();
                enabled = true;
            }
            catch (Exception exception)
            {
                _generatedPostfix = null;
                logger.Warning($"[Scourge] optional SAIN generated-default hook was not enabled: {exception.Message}");
            }

            try
            {
                new ComposeCustomPatch(schema.ComposeCustom).Enable();
                enabled = true;
            }
            catch (Exception exception)
            {
                _composeCustomPostfix = null;
                logger.Warning($"[Scourge] optional SAIN custom-preset hook was not enabled: {exception.Message}");
            }

            if (enabled)
            {
                logger.Info("[Scourge] optional SAIN defaults enabled for Scourge roles.");
            }
        }
        catch (Exception exception)
        {
            logger.Warning(
                $"[Scourge] optional SAIN defaults disabled because the installed SAIN schema is unsupported: {exception.Message}");
        }
    }

    private void ApplyGenerated(object? result)
    {
        if (result is null)
        {
            return;
        }

        try
        {
            SainSchema.ApplyMissingPersonalities(result);
            SainSchema.ApplyTuning(result);
        }
        catch (Exception exception)
        {
            logger.Warning($"[Scourge] SAIN generated-default tuning was skipped: {exception.Message}");
        }
    }

    private void ApplyCustom(object? result)
    {
        if (result is null)
        {
            return;
        }

        try
        {
            // Stored role groups and existing personality values, including
            // None, remain authoritative; only missing Scourge mappings are added.
            SainSchema.ApplyMissingPersonalities(result);
        }
        catch (Exception exception)
        {
            logger.Warning($"[Scourge] SAIN custom-preset personality defaults were skipped: {exception.Message}");
        }
    }

    private sealed class GeneratePatch(MethodBase targetMethod) : AbstractPatch($"{ModIdentity.Guid}.sain-defaults.generate")
    {
        protected override MethodBase GetTargetMethod() => targetMethod;

        [PatchPostfix]
        private static void Postfix(object? __result) => _generatedPostfix?.Invoke(__result);
    }

    private sealed class ComposeCustomPatch(MethodBase targetMethod) : AbstractPatch($"{ModIdentity.Guid}.sain-defaults.compose-custom")
    {
        protected override MethodBase GetTargetMethod() => targetMethod;

        [PatchPostfix]
        private static void Postfix(object? __result) => _composeCustomPostfix?.Invoke(__result);
    }

    private sealed class SainSchema
    {
        private const string ServiceName = "SAINServerMod.Generators.PresetGenerationService";
        private const string GeneratedName = "SAINServerMod.Models.Preset.GeneratedPreset";
        private const string BundleName = "SAIN.Preset.Shared.Preset.SAINPresetBundle";
        private const string BotDifficultyName = "SAIN.Preset.Shared.Enums.ESainBotDifficulty";
        private const string PresetDifficultyName = "SAIN.Preset.Shared.Enums.SAINDifficulty";
        private const string RoleName = "SAIN.Preset.Shared.Enums.ESainWildSpawnType";
        private const string PersonalityName = "SAIN.Preset.Shared.Models.Preset.Personalities.EPersonality";
        private const string GroupName = "SAIN.Preset.Shared.BotSettings.SAINSettings.SAINSettingsGroupClass";

        private static readonly IReadOnlyDictionary<int, string> Personalities =
            new Dictionary<int, string>
            {
                [Manimal.Scourge.ScourgeRoles.BossValue] = "Chad",
                [Manimal.Scourge.ScourgeRoles.FollowerValue] = "GigaChad",
            };

        // Approximate Twitch Players 3.1.2 Wedge numeric profile values; this
        // is not a retail profile port. SAIN's preset tuner supplies all other fields.
        private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<ScalarDefault>>> RawDefaults =
            new Dictionary<int, IReadOnlyDictionary<string, IReadOnlyList<ScalarDefault>>>
            {
                [Manimal.Scourge.ScourgeRoles.BossValue] = RoleDefaults(.40f, .12f, 50f, .38f, 2.2f, 1.1f),
                [Manimal.Scourge.ScourgeRoles.FollowerValue] = RoleDefaults(.30f, .08f, 60f, .33f, 2f, 1f),
            };

        private static SainSchema? _active;

        private SainSchema(
            MethodBase generate,
            MethodBase composeCustom,
            Type generatedType,
            Type difficultyType,
            Type roleType,
            Type personalityType,
            PropertyInfo generatedBotSettings,
            PropertyInfo generatedGlobalSettings,
            FieldInfo bundleGlobalSettings,
            FieldInfo groupSettings,
            FieldInfo globalMind,
            FieldInfo personalitiesByRole,
            IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<BoundScalar>>> defaults)
        {
            Generate = generate;
            ComposeCustom = composeCustom;
            GeneratedType = generatedType;
            DifficultyType = difficultyType;
            RoleType = roleType;
            PersonalityType = personalityType;
            GeneratedBotSettings = generatedBotSettings;
            GeneratedGlobalSettings = generatedGlobalSettings;
            BundleGlobalSettings = bundleGlobalSettings;
            GroupSettings = groupSettings;
            GlobalMind = globalMind;
            PersonalitiesByRole = personalitiesByRole;
            BoundDefaults = defaults;
        }

        public MethodBase Generate { get; }
        public MethodBase ComposeCustom { get; }
        private Type GeneratedType { get; }
        private Type DifficultyType { get; }
        private Type RoleType { get; }
        private Type PersonalityType { get; }
        private PropertyInfo GeneratedBotSettings { get; }
        private PropertyInfo GeneratedGlobalSettings { get; }
        private FieldInfo BundleGlobalSettings { get; }
        private FieldInfo GroupSettings { get; }
        private FieldInfo GlobalMind { get; }
        private FieldInfo PersonalitiesByRole { get; }
        private IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<BoundScalar>>> BoundDefaults { get; }

        public static SainSchema Resolve(Assembly sainAssembly)
        {
            var serviceType = RequireType(sainAssembly, ServiceName);
            var generatedType = RequireType(sainAssembly, GeneratedName);
            var sharedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetType(BundleName, false) is not null)
                ?? throw new MissingMemberException(BundleName);
            var bundleType = RequireType(sharedAssembly, BundleName);
            var botDifficultyType = RequireEnum(sharedAssembly, BotDifficultyName, "easy", "normal", "hard", "impossible");
            var presetDifficultyType = RequireEnum(
                sharedAssembly,
                PresetDifficultyName,
                "none",
                "easy",
                "lesshard",
                "hard",
                "harderpmcs",
                "veryhard",
                "deathwish",
                "custom");
            var roleType = RequireEnum(sharedAssembly, RoleName);
            var personalityType = RequireEnum(sharedAssembly, PersonalityName, "None", "Chad", "GigaChad");
            var generate = FindMethod(serviceType, "Generate", generatedType, presetDifficultyType, typeof(string), typeof(string))
                ?? throw new MissingMethodException("SAIN PresetGenerationService.Generate");
            var composeCustom = FindMethod(serviceType, "ComposeCustom", bundleType, bundleType)
                ?? throw new MissingMethodException("SAIN PresetGenerationService.ComposeCustom");

            var generatedBotSettings = RequireProperty(generatedType, "BotSettings");
            var generatedGlobalSettings = RequireProperty(generatedType, "GlobalSettings");
            var bundleGlobalSettings = RequireField(bundleType, "GlobalSettings");
            var botDictionary = DictionaryTypes(generatedBotSettings.PropertyType);
            if (botDictionary.Key != roleType || botDictionary.Value.FullName != GroupName)
            {
                throw new InvalidDataException("SAIN GeneratedPreset.BotSettings schema changed.");
            }

            var groupSettings = RequireField(botDictionary.Value, "Settings");
            var settingsDictionary = DictionaryTypes(groupSettings.FieldType);
            if (settingsDictionary.Key != botDifficultyType)
            {
                throw new InvalidDataException("SAIN settings group is not keyed by ESainBotDifficulty.");
            }

            var settingsType = settingsDictionary.Value;
            var categoryFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal)
            {
                ["Core"] = RequireField(settingsType, "Core"),
                ["Aiming"] = RequireField(settingsType, "Aiming"),
                ["Mind"] = RequireField(settingsType, "Mind"),
                ["Shoot"] = RequireField(settingsType, "Shoot"),
            };
            var boundDefaults = BindDefaults(categoryFields);

            var globalMind = RequireField(generatedGlobalSettings.PropertyType, "Mind");
            if (bundleGlobalSettings.FieldType != generatedGlobalSettings.PropertyType)
            {
                throw new InvalidDataException("SAIN preset bundle global-settings schema changed.");
            }

            var personalitiesByRole = RequireField(globalMind.FieldType, "PERS_BOSSES");
            var personalityDictionary = DictionaryTypes(personalitiesByRole.FieldType);
            if (personalityDictionary.Key != roleType || personalityDictionary.Value != personalityType)
            {
                throw new InvalidDataException("SAIN PERS_BOSSES schema changed.");
            }

            var schema = new SainSchema(
                generate,
                composeCustom,
                generatedType,
                botDifficultyType,
                roleType,
                personalityType,
                generatedBotSettings,
                generatedGlobalSettings,
                bundleGlobalSettings,
                groupSettings,
                globalMind,
                personalitiesByRole,
                boundDefaults);
            _active = schema;
            return schema;
        }

        public static void ApplyTuning(object generatedPreset)
        {
            var schema = _active ?? throw new InvalidOperationException("SAIN schema is not active.");
            var bots = schema.GeneratedBotSettings.GetValue(generatedPreset) as IDictionary
                ?? throw new InvalidDataException("SAIN GeneratedPreset.BotSettings is unavailable.");
            foreach (var (role, roleDefaults) in schema.BoundDefaults)
            {
                var roleKey = Enum.ToObject(schema.RoleType, role);
                if (!bots.Contains(roleKey) || bots[roleKey] is not { } group)
                {
                    continue;
                }

                var settings = schema.GroupSettings.GetValue(group) as IDictionary
                    ?? throw new InvalidDataException("SAIN Scourge settings are unavailable.");
                foreach (var (difficultyName, scalarDefaults) in roleDefaults)
                {
                    var difficulty = Enum.Parse(schema.DifficultyType, difficultyName);
                    if (!settings.Contains(difficulty) || settings[difficulty] is not { } values)
                    {
                        continue;
                    }

                    foreach (var scalar in scalarDefaults)
                    {
                        var category = scalar.CategoryField.GetValue(values)
                            ?? throw new InvalidDataException($"SAIN category '{scalar.CategoryField.Name}' is null.");
                        scalar.ValueField.SetValue(category, scalar.Value);
                    }
                }
            }
        }

        public static void ApplyMissingPersonalities(object preset)
        {
            var schema = _active ?? throw new InvalidOperationException("SAIN schema is not active.");
            var globalSettings = schema.GeneratedType.IsInstanceOfType(preset)
                ? schema.GeneratedGlobalSettings.GetValue(preset)
                : schema.BundleGlobalSettings.GetValue(preset);
            if (globalSettings is null)
            {
                return;
            }

            var mind = schema.GlobalMind.GetValue(globalSettings)
                ?? throw new InvalidDataException("SAIN GlobalSettings.Mind is unavailable.");
            var personalities = schema.PersonalitiesByRole.GetValue(mind) as IDictionary
                ?? throw new InvalidDataException("SAIN PERS_BOSSES is unavailable.");
            foreach (var (role, personalityName) in Personalities)
            {
                var roleKey = Enum.ToObject(schema.RoleType, role);
                if (!personalities.Contains(roleKey))
                {
                    personalities.Add(roleKey, Enum.Parse(schema.PersonalityType, personalityName));
                }
            }
        }

        private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<BoundScalar>>> BindDefaults(
            IReadOnlyDictionary<string, FieldInfo> categoryFields)
        {
            var result = new Dictionary<int, IReadOnlyDictionary<string, IReadOnlyList<BoundScalar>>>();
            foreach (var (role, difficultyDefaults) in RawDefaults)
            {
                var roleResult = new Dictionary<string, IReadOnlyList<BoundScalar>>(StringComparer.Ordinal);
                foreach (var (difficulty, scalars) in difficultyDefaults)
                {
                    roleResult[difficulty] = scalars.Select(scalar =>
                    {
                        var categoryField = categoryFields.TryGetValue(scalar.Category, out var category)
                            ? category
                            : throw new MissingFieldException("SAINSettingsClass", scalar.Category);
                        var valueField = categoryField.FieldType.GetField(
                            scalar.Field,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? throw new MissingFieldException(categoryField.FieldType.FullName, scalar.Field);
                        return new BoundScalar(categoryField, valueField, ConvertScalar(scalar.Value, valueField.FieldType));
                    }).ToArray();
                }

                result[role] = roleResult;
            }

            return result;
        }

        private static object ConvertScalar(object value, Type targetType)
        {
            if (targetType == typeof(bool))
            {
                return value is bool
                    ? value
                    : throw new InvalidDataException($"SAIN bool field received '{value.GetType().Name}'.");
            }

            if (!IsPrimitiveNumber(targetType) || value is not IConvertible)
            {
                throw new InvalidDataException($"SAIN tuning field type '{targetType.FullName}' is unsupported.");
            }

            // Check before conversion: Convert.ChangeType rounds fractional
            // floating-point values when a future schema changes a field to int.
            if (targetType != typeof(float) && targetType != typeof(double) && targetType != typeof(decimal)
                && Convert.ToDouble(value, CultureInfo.InvariantCulture) % 1d != 0d)
            {
                throw new InvalidDataException($"SAIN tuning value '{value}' is not integral for '{targetType.FullName}'.");
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture)!;
        }

        private static bool IsPrimitiveNumber(Type type) => type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long)
            || type == typeof(ulong) || type == typeof(float) || type == typeof(double)
            || type == typeof(decimal);

        private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, false)
            ?? throw new MissingMemberException(name);

        private static Type RequireEnum(Assembly assembly, string name, params string[] values)
        {
            var type = RequireType(assembly, name);
            if (!type.IsEnum || values.Any(value => !Enum.IsDefined(type, value)))
            {
                throw new InvalidDataException($"SAIN enum '{name}' changed.");
            }

            return type;
        }

        private static PropertyInfo RequireProperty(Type type, string name) => type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(type.FullName, name);

        private static FieldInfo RequireField(Type type, string name) => type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);

        private static MethodBase? FindMethod(Type type, string name, Type returnType, params Type[] parameters) => type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == name
                && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters));

        private static (Type Key, Type Value) DictionaryTypes(Type type)
        {
            var dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    ?? throw new InvalidDataException($"SAIN member '{type.FullName}' is not a dictionary.");
            var arguments = dictionary.GetGenericArguments();
            return (arguments[0], arguments[1]);
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<ScalarDefault>> RoleDefaults(
            float accuracySpeed,
            float scatteringPerMeter,
            float aimForHeadChance,
            float fasterCqbMinimum,
            float maxAimTime,
            float recoilMultiplier)
        {
            var common = new ScalarDefault[]
            {
                new("Core", "VisibleAngle", 180f),
                new("Core", "GainSightCoef", 0.20f),
                new("Core", "AccuratySpeed", accuracySpeed),
                new("Core", "ScatteringPerMeter", scatteringPerMeter),
                new("Core", "ScatteringClosePerMeter", 0.12f),
                new("Core", "HearingDistanceMulti", 1f),
                new("Aiming", "OverrideAimTargetWeights", false),
                new("Aiming", "AimForHead", true),
                new("Aiming", "AimForHeadChance", aimForHeadChance),
                new("Aiming", "DistanceAimTimeMultiplier", 1f),
                new("Aiming", "AngleAimTimeMultiplier", 1f),
                new("Aiming", "FasterCQBReactionsDistance", 30f),
                new("Aiming", "FasterCQBReactionsMinimum", fasterCqbMinimum),
                new("Aiming", "MAX_AIMING_UPGRADE_BY_TIME", 0.25f),
                new("Aiming", "DIST_TO_SHOOT_NO_OFFSET", 3f),
                new("Aiming", "COEF_IF_MOVE", 1.5f),
                new("Aiming", "MAX_AIM_TIME", maxAimTime),
                new("Aiming", "DAMAGE_TO_DISCARD_AIM_0_100", 100f),
                new("Aiming", "BASE_HIT_AFFECTION_DELAY_SEC", 0.65f),
                new("Aiming", "MIN_TIME_DISCARD_AIM_SEC", 0.5f),
                new("Aiming", "MAX_TIME_DISCARD_AIM_SEC", 1.5f),
                new("Aiming", "FIRST_CONTACT_ADD_SEC", 0.2f),
                new("Mind", "WeaponProficiency", 0.5f),
                new("Shoot", "RecoilMultiplier", recoilMultiplier),
                new("Shoot", "MaxPointFireDistance", 150f),
            };
            var result = new Dictionary<string, IReadOnlyList<ScalarDefault>>(StringComparer.Ordinal);
            foreach (var (difficulty, distance, fasterCqb) in new[]
            {
                ("easy", 150f, false),
                ("normal", 225f, true),
                ("hard", 250f, true),
                ("impossible", 275f, true),
            })
            {
                result[difficulty] = common.Concat(
                [
                    new ScalarDefault("Core", "VisibleDistance", distance),
                    new ScalarDefault("Aiming", "FasterCQBReactions", fasterCqb),
                ]).ToArray();
            }

            return result;
        }

        private sealed record ScalarDefault(string Category, string Field, object Value);
        private sealed record BoundScalar(FieldInfo CategoryField, FieldInfo ValueField, object Value);
    }
}
