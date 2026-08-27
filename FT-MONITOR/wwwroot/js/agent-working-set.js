// Shared client-side helper for the Agent "Working Set" feature.
//
// The working set is the list of work items / feedback tickets the user
// has pinned via the "Send to Agent" button on top-bugs.html and
// top-feedback.html. It is read by /agent.html and forwarded to the
// /api/agent/run endpoint so the agent grounds every answer on those ids.
//
// Items are stored in localStorage under a single key. Each item is a
// minimal projection (id, title, type, state, priority, areaPath, assignedTo)
// — enough for the agent prompt and the sidebar UI.

(function () {
    const KEY = 'ftmon-agent-workingset';

    function read() {
        try {
            const raw = localStorage.getItem(KEY);
            if (!raw) return [];
            const arr = JSON.parse(raw);
            return Array.isArray(arr) ? arr : [];
        } catch {
            return [];
        }
    }

    function write(items) {
        localStorage.setItem(KEY, JSON.stringify(items || []));
    }

    function clear() {
        localStorage.removeItem(KEY);
    }

    function add(items) {
        if (!items || !items.length) return read();
        const current = read();
        const map = new Map(current.map(i => [i.id, i]));
        items.forEach(i => { if (i && typeof i.id === 'number') map.set(i.id, i); });
        const merged = Array.from(map.values());
        write(merged);
        return merged;
    }

    function remove(id) {
        const next = read().filter(i => i.id !== id);
        write(next);
        return next;
    }

    /**
     * Convert a DevOps work-item record (as returned by /api/top-bugs and
     * /api/top-feedback) into the compact shape we store. Falls back gracefully
     * if some fields are missing.
     */
    function projectWorkItem(wi, type) {
        if (!wi || !wi.fields) return null;
        return {
            id: wi.id,
            title: wi.fields.title || '',
            type: type || 'WorkItem',
            state: wi.fields.state || '',
            priority: typeof wi.fields.priority === 'number' ? wi.fields.priority : null,
            areaPath: wi.fields.areaPath || '',
            assignedTo: wi.fields.assignedTo ? wi.fields.assignedTo.displayName : ''
        };
    }

    window.AgentWorkingSet = { read, write, clear, add, remove, projectWorkItem, KEY };
})();
