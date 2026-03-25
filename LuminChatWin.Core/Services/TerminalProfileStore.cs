using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public sealed class TerminalProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    private readonly Func<string> _pathAccessor;

    public TerminalProfileStore(Func<string> pathAccessor)
    {
        _pathAccessor = pathAccessor;
    }

    private string StorePath => _pathAccessor();

    public IReadOnlyList<TerminalSessionProfile> List()
    {
        return LoadInternal()
            .OrderBy(profile => profile.Kind)
            .ThenBy(profile => profile.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TerminalSessionProfile Save(TerminalSessionProfile profile)
    {
        var profiles = LoadInternal();
        var now = DateTime.UtcNow.ToString("O");
        profile.ProfileId = string.IsNullOrWhiteSpace(profile.ProfileId) ? Guid.NewGuid().ToString("N") : profile.ProfileId;
        profile.CreatedAt = string.IsNullOrWhiteSpace(profile.CreatedAt) ? now : profile.CreatedAt;

        var existingIndex = profiles.FindIndex(item => string.Equals(item.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex < 0)
        {
            existingIndex = profiles.FindIndex(item =>
                item.Kind == profile.Kind &&
                string.Equals(item.Descriptor, profile.Descriptor, StringComparison.OrdinalIgnoreCase));
        }

        if (existingIndex >= 0)
        {
            profile.CreatedAt = profiles[existingIndex].CreatedAt;
            profiles[existingIndex] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        SaveInternal(profiles);
        return profile;
    }

    public void Delete(string profileId)
    {
        var profiles = LoadInternal();
        profiles.RemoveAll(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        SaveInternal(profiles);
    }

    public TerminalSessionProfile Rename(string profileId, string newTitle)
    {
        var profiles = LoadInternal();
        var profile = profiles.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown terminal profile: {profileId}");
        profile.Title = newTitle;
        SaveInternal(profiles);
        return profile;
    }

    public TerminalSessionProfile SetApiShared(string profileId, bool enabled)
    {
        var profiles = LoadInternal();
        var profile = profiles.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown terminal profile: {profileId}");
        profile.ApiShared = enabled;
        SaveInternal(profiles);
        return profile;
    }

    public TerminalSessionProfile SetSshShared(string profileId, bool enabled)
    {
        var profiles = LoadInternal();
        var profile = profiles.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown terminal profile: {profileId}");
        profile.SshShared = enabled;
        SaveInternal(profiles);
        return profile;
    }

    public TerminalSessionProfile Touch(string profileId)
    {
        var profiles = LoadInternal();
        var profile = profiles.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown terminal profile: {profileId}");
        profile.LastUsedAt = DateTime.UtcNow.ToString("O");
        SaveInternal(profiles);
        return profile;
    }

    private List<TerminalSessionProfile> LoadInternal()
    {
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            SaveInternal([]);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<TerminalSessionProfile>>(json, JsonOptions) ?? [];
    }

    private void SaveInternal(List<TerminalSessionProfile> profiles)
    {
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(profiles, JsonOptions));
    }
}