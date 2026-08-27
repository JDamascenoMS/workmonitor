using FT_MONITOR.Helpers;
using System.Text.Json;

namespace FT_MONITOR.Services
{
    public class ParametersService
    {
        private readonly string _filePath;
        private readonly string _queriesFilePath;
        private readonly object _lock = new();
        private Dictionary<string, string> _areaPaths;
        private Dictionary<string, string> _savedQueries;

        public ParametersService(IWebHostEnvironment env)
        {
            var dataDir = Path.Combine(env.ContentRootPath, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.Combine(dataDir, "parameters.json");
            _queriesFilePath = Path.Combine(dataDir, "saved-queries.json");
            _areaPaths = Load();
            _savedQueries = LoadQueries();
            SyncConstants();
        }

        public Dictionary<string, string> GetAreaPaths()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_areaPaths);
            }
        }

        public void AddAreaPath(string name, string path)
        {
            lock (_lock)
            {
                if (_areaPaths.ContainsKey(name))
                    throw new InvalidOperationException($"Area path '{name}' already exists.");

                _areaPaths[name] = path;
                Save();
                SyncConstants();
            }
        }

        public void UpdateAreaPath(string name, string newName, string path)
        {
            lock (_lock)
            {
                if (!_areaPaths.ContainsKey(name))
                    throw new KeyNotFoundException($"Area path '{name}' not found.");

                if (name != newName)
                {
                    if (_areaPaths.ContainsKey(newName))
                        throw new InvalidOperationException($"Area path '{newName}' already exists.");

                    _areaPaths.Remove(name);
                }

                _areaPaths[newName] = path;
                Save();
                SyncConstants();
            }
        }

        public void DeleteAreaPath(string name)
        {
            lock (_lock)
            {
                if (!_areaPaths.Remove(name))
                    throw new KeyNotFoundException($"Area path '{name}' not found.");

                Save();
                SyncConstants();
            }
        }

        public Dictionary<string, string> GetSavedQueries()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_savedQueries);
            }
        }

        public void AddSavedQuery(string name, string queryId)
        {
            lock (_lock)
            {
                if (_savedQueries.ContainsKey(name))
                    throw new InvalidOperationException($"Saved query '{name}' already exists.");

                _savedQueries[name] = queryId;
                SaveQueries();
            }
        }

        public void UpdateSavedQuery(string name, string newName, string queryId)
        {
            lock (_lock)
            {
                if (!_savedQueries.ContainsKey(name))
                    throw new KeyNotFoundException($"Saved query '{name}' not found.");

                if (name != newName)
                {
                    if (_savedQueries.ContainsKey(newName))
                        throw new InvalidOperationException($"Saved query '{newName}' already exists.");

                    _savedQueries.Remove(name);
                }

                _savedQueries[newName] = queryId;
                SaveQueries();
            }
        }

        public void DeleteSavedQuery(string name)
        {
            lock (_lock)
            {
                if (!_savedQueries.Remove(name))
                    throw new KeyNotFoundException($"Saved query '{name}' not found.");

                SaveQueries();
            }
        }

        private Dictionary<string, string> Load()
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null && data.Count > 0)
                    return data;
            }

            return new Dictionary<string, string>(Constants.ProjectAreaPath);
        }

        private void Save()
        {
            var json = JsonSerializer.Serialize(_areaPaths, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }

        private Dictionary<string, string> LoadQueries()
        {
            if (File.Exists(_queriesFilePath))
            {
                var json = File.ReadAllText(_queriesFilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null)
                    return data;
            }

            return new Dictionary<string, string>();
        }

        private void SaveQueries()
        {
            var json = JsonSerializer.Serialize(_savedQueries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_queriesFilePath, json);
        }

        private void SyncConstants()
        {
            Constants.ProjectAreaPath = new Dictionary<string, string>(_areaPaths);
        }
    }
}
