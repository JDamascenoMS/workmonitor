using System.Text.Json;

namespace WorkMonitor.Agent
{
    /// <summary>
    /// File-backed CRUD store for AgentSkill. Persists to App_Data/skills.json,
    /// caches in memory, and serializes writes through a SemaphoreSlim.
    /// </summary>
    public class SkillStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private List<AgentSkill> _cache;

        public SkillStore(string storageRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storageRootPath);
            var dir = Path.Combine(storageRootPath, "App_Data");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "skills.json");
            _cache = LoadFromDisk();
        }

        public IReadOnlyList<AgentSkill> List()
        {
            _writeLock.Wait();
            try
            {
                return _cache.ToList();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public AgentSkill? Get(string id)
        {
            _writeLock.Wait();
            try
            {
                return _cache.FirstOrDefault(
                    skill => string.Equals(
                        skill.Id,
                        id,
                        StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<AgentSkill> CreateAsync(AgentSkill skill)
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
                throw new ArgumentException("Skill name is required.", nameof(skill));

            skill.Id = Guid.NewGuid().ToString("N");
            skill.CreatedAt = DateTime.UtcNow;

            await _writeLock.WaitAsync();
            try
            {
                _cache.Add(skill);
                await SaveToDiskAsync();
            }
            finally { _writeLock.Release(); }

            return skill;
        }

        public async Task<bool> DeleteAsync(string id)
        {
            await _writeLock.WaitAsync();
            try
            {
                var removed = _cache.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) await SaveToDiskAsync();
                return removed > 0;
            }
            finally { _writeLock.Release(); }
        }

        // -----------------------------------------------------------------
        private List<AgentSkill> LoadFromDisk()
        {
            if (!File.Exists(_filePath)) return new List<AgentSkill>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<AgentSkill>>(json, JsonOpts) ?? new();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Skill store '{_filePath}' contains invalid JSON.",
                    ex);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Skill store '{_filePath}' could not be read.",
                    ex);
            }
        }

        private async Task SaveToDiskAsync()
        {
            var json = JsonSerializer.Serialize(_cache, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json);
        }
    }
}
