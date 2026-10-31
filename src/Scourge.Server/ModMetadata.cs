using System.Collections.Generic;
using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace Manimal.Scourge.Server;

[UsedImplicitly]
public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = ModIdentity.Guid;
    public string Name { get; init; } = ModIdentity.Name;
    public string Author { get; init; } = ModIdentity.Author;
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new(ModIdentity.Version);
    public Range SptVersion { get; init; } = new(ModIdentity.SptRange);
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        [ModIdentity.MoreBotsGuid] = new($"~{ModIdentity.MoreBotsVersion}"),
        [ModIdentity.WttGuid] = new($"~{ModIdentity.WttVersion}"),
        [ModIdentity.CommonLibGuid] = new($"^{ModIdentity.CommonLibVersion}"),
    };
    public string? Url { get; init; } = string.IsNullOrEmpty(ModIdentity.SourceUrl) ? null : ModIdentity.SourceUrl;
    public string License { get; init; } = ModIdentity.License;
}
