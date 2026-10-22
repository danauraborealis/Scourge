using System.Text.Json;
using System.Text.Json.Serialization;

namespace Manimal.Scourge.Server;

internal sealed class ScourgeConfig
{
    public bool Enabled { get; set; } = true;
    public double SpawnChance { get; set; } = 100;
    public int EscortCount { get; set; } = 4;
    [JsonConverter(typeof(SpawnZonesConverter))]
    public List<string> SpawnZones { get; set; } = ["BotZoneFloor1", "BotZoneFloor2"];
    public double SpawnTime { get; set; } = -1;

    public static ScourgeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ScourgeConfig();
        }

        var config = JsonSerializer.Deserialize<ScourgeConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Scourge config '{path}' is empty.");

        config.Validate(path);
        return config;
    }

    private void Validate(string path)
    {
        if (SpawnChance is < 0 or > 100)
        {
            throw new InvalidDataException($"Scourge config '{path}' has SpawnChance outside 0-100.");
        }

        if (EscortCount is < 0 or > 8)
        {
            throw new InvalidDataException($"Scourge config '{path}' has EscortCount outside 0-8.");
        }

        if (SpawnZones is null || SpawnZones.Count is < 1 or > 8 || SpawnZones.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Scourge config '{path}' must contain 1-8 non-empty SpawnZones.");
        }

        if (double.IsNaN(SpawnTime) || double.IsInfinity(SpawnTime) || SpawnTime < -1 || SpawnTime > 180)
        {
            throw new InvalidDataException($"Scourge config '{path}' has SpawnTime outside -1-180.");
        }
    }
}

/// <summary>
/// Accepts both the human-friendly comma-separated form and the JSON array form.
/// The generated package uses the former, while arrays are convenient for hand-edited configs.
/// </summary>
internal sealed class SpawnZonesConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var zones = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("SpawnZones array entries must be strings.");
                }

                zones.Add(reader.GetString()!);
            }

            return zones;
        }

        throw new JsonException("SpawnZones must be a comma-separated string or an array of strings.");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(string.Join(',', value));
    }
}
