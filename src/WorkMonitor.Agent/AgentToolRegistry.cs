namespace WorkMonitor.Agent
{
    /// <summary>
    /// In-process registry of every IAgentTool registered with DI.
    /// Resolved as a singleton; agents look up tools by name during the
    /// function-calling loop.
    /// </summary>
    public class AgentToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _byName;

        public AgentToolRegistry(IEnumerable<IAgentTool> tools)
        {
            _byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<IAgentTool> All => _byName.Values;

        public bool TryGet(string name, out IAgentTool tool)
            => _byName.TryGetValue(name, out tool!);
    }
}
