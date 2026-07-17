using System.Text.Json;
using System.IO;

namespace GameControllerMapper;

public sealed class ProfileStore
{
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameControllerMapper",
        "profiles.json");

    public ProfileDocument Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), _json);
                if (loaded?.Profiles.Count > 0)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch (JsonException)
        {
            // Keep the unreadable file intact until the user explicitly saves a valid profile.
        }
        catch (IOException)
        {
        }

        var profile = new MappingProfile();
        return new ProfileDocument { SelectedProfileId = profile.Id, Profiles = [profile] };
    }

    public void Save(ProfileDocument document)
    {
        Normalize(document);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, _json));
        File.Move(temporary, FilePath, true);
    }

    public string Serialize(ProfileDocument document) => JsonSerializer.Serialize(document, _json);
    public ProfileDocument? Deserialize(string json) => JsonSerializer.Deserialize<ProfileDocument>(json, _json);

    private static void Normalize(ProfileDocument document)
    {
        if (document.Profiles.Count == 0) document.Profiles.Add(new MappingProfile());
        foreach (var profile in document.Profiles)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "未命名配置" : profile.Name.Trim();
            profile.Bindings ??= new(StringComparer.Ordinal);
            profile.StickDeadzone = Math.Clamp(profile.StickDeadzone, 0, 0.9);
            profile.DirectionThreshold = Math.Clamp(profile.DirectionThreshold, 0.1, 1);
            profile.TriggerThreshold = Math.Clamp(profile.TriggerThreshold, 0.05, 1);
            profile.MouseSpeed = Math.Clamp(profile.MouseSpeed, 50, 5000);
        }

        if (document.Profiles.All(x => x.Id != document.SelectedProfileId))
            document.SelectedProfileId = document.Profiles[0].Id;
    }
}
