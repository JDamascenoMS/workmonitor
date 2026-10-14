(function () {
    'use strict';

    const elements = {};
    [
        'conversationList', 'newConversation', 'refreshConversations', 'loadOlderConversations', 'conversationTitle',
        'conversationMeta', 'reloadConversation', 'transcript', 'status', 'statusMessage',
        'statusSignIn', 'composer', 'prompt', 'sendBtn', 'cancelBtn', 'wsList',
        'wsCountTag', 'wsClear', 'wsRefresh', 'contextHint'
    ].forEach(id => { elements[id] = document.getElementById(id); });

    const state = {
        authenticated: false,
        currentId: null,
        conversations: [],
        messages: [],
        viewVersion: 0,
        viewController: null,
        listVersion: 0,
        listController: null,
        listOffset: 0,
        listHasMore: false,
        listLoading: false,
        listMode: null,
        loading: false,
        turn: null,
        recoveryController: null
    };
    const conversationPageSize = 20;
    const eventKinds = new Set([
        'turnStarted', 'textDelta', 'toolStarted', 'toolCompleted', 'warning', 'error', 'completed'
    ]);

    class HttpError extends Error {
        constructor(status, body) {
            super(asText(body && body.error) || 'HTTP ' + status);
            this.status = status;
            this.errorCode = asText(body && body.errorCode);
        }
    }

    function node(tag, className, text) {
        const element = document.createElement(tag);
        if (className) element.className = className;
        if (text !== undefined) element.textContent = asText(text);
        return element;
    }

    function asText(value) {
        if (value === null || value === undefined) return '';
        return typeof value === 'string' ? value : JSON.stringify(value);
    }

    function dateLabel(value) {
        if (!value) return '';
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '';
        return date.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    }

    function showStatus(message, type, signIn) {
        elements.status.className = 'status' + (type ? ' ' + type : '');
        elements.status.setAttribute('role', type === 'error' ? 'alert' : 'status');
        elements.statusMessage.textContent = message;
        elements.statusSignIn.hidden = !signIn;
    }

    function showFailure(error) {
        const detail = error.message || 'The request could not be completed.';
        const code = error.errorCode ? ' (' + error.errorCode + ')' : '';
        if (error.status === 401) {
            state.authenticated = false;
            state.conversations = [];
            state.listOffset = 0;
            state.listHasMore = false;
            renderConversations();
            renderUserInfo('userInfo', { linkColor: '#ffffff' });
            showStatus('Sign in to continue. Your session may have expired. ' + detail + code, 'warning', true);
        } else if (error.status === 403) {
            showStatus('Access denied. Reload to refresh the security token. If this persists, sign in again to grant consent, or ask your administrator to check Azure DevOps access and the app registration. ' + detail + code, 'error', true);
        } else if (error.status === 409) {
            showStatus('A turn is already in progress. Wait for it to stop, then use Reload before sending another message. ' + detail + code, 'warning');
        } else {
            showStatus(detail + code, 'error');
        }
        updateControls();
    }

    async function checkResponse(response) {
        if (response.ok) return response;
        let body = null;
        try { body = await response.json(); } catch { /* Some infrastructure errors have no JSON body. */ }
        throw new HttpError(response.status, body);
    }

    async function getJson(url, signal) {
        const response = await checkResponse(await authFetch(url, { signal, cache: 'no-store', headers: { Accept: 'application/json' } }));
        return response.json();
    }

    async function post(url, body, signal, accept) {
        return checkResponse(await authFetch(url, {
            method: 'POST',
            signal,
            headers: {
                'Content-Type': 'application/json',
                Accept: accept || 'application/json'
            },
            body: JSON.stringify(body)
        }));
    }

    function updateControls() {
        const busy = state.loading || !!state.turn;
        elements.newConversation.disabled = !state.authenticated || state.loading;
        elements.refreshConversations.disabled = !state.authenticated;
        elements.loadOlderConversations.hidden = !state.authenticated || !state.listHasMore;
        elements.loadOlderConversations.disabled = !state.authenticated || state.listLoading || !state.listHasMore;
        elements.loadOlderConversations.textContent = state.listLoading && state.listMode === 'older'
            ? 'Loading\u2026' : 'Load older conversations';
        elements.reloadConversation.disabled = !state.authenticated || !state.currentId || busy;
        elements.prompt.disabled = !state.authenticated || busy;
        elements.sendBtn.disabled = !state.authenticated || busy || !elements.prompt.value.trim();
        const currentTurn = state.turn && state.turn.version === state.viewVersion;
        elements.cancelBtn.hidden = !currentTurn;
        elements.cancelBtn.disabled = !!(currentTurn && state.turn.cancelled);
        elements.cancelBtn.textContent = currentTurn && state.turn.cancelled ? 'Canceling\u2026' : 'Cancel response';
        elements.transcript.setAttribute('aria-busy', busy ? 'true' : 'false');
    }

    function readWorkingSet() {
        return AgentWorkingSet.read()
            .filter(item => item && Number.isSafeInteger(item.id) && item.id > 0)
            .map(item => ({
                id: item.id,
                title: asText(item.title),
                type: asText(item.type) || 'WorkItem',
                state: asText(item.state),
                priority: Number.isSafeInteger(item.priority) ? item.priority : null,
                areaPath: asText(item.areaPath),
                assignedTo: asText(item.assignedTo)
            }));
    }

    function renderWorkingSet() {
        const items = readWorkingSet();
        elements.wsList.replaceChildren();
        elements.wsCountTag.textContent = String(items.length);
        elements.wsCountTag.hidden = !items.length;
        elements.wsClear.disabled = !items.length;
        elements.contextHint.textContent = items.length
            ? items.length + ' pinned item' + (items.length === 1 ? '' : 's') + ' included with your next message.'
            : 'No pinned items included.';
        if (items.length > 50) elements.contextHint.textContent += ' Remove items to reach the 50-item limit before sending.';
        if (!items.length) {
            elements.wsList.append(node('p', 'subtle', 'No items pinned yet. Add context from the Bugs or Feedback dashboard.'));
        }
        items.forEach(item => {
            const card = node('div', 'ws-item');
            const head = node('div', 'ws-head');
            head.append(node('span', '', '#' + item.id));
            const remove = node('button', 'ws-remove', '\u00d7');
            remove.type = 'button';
            remove.setAttribute('aria-label', 'Remove item ' + item.id + ' from working set');
            remove.addEventListener('click', () => {
                try {
                    AgentWorkingSet.remove(item.id);
                    renderWorkingSet();
                } catch { showStatus('The local working set could not be updated. Check browser storage permissions.', 'error'); }
            });
            head.append(remove);
            card.append(head, node('p', 'ws-title', item.title || 'Untitled work item'));
            card.append(node('div', 'ws-meta', [item.type, item.state || 'Unknown state', 'P' + (item.priority === null ? '?' : item.priority), item.assignedTo || 'Unassigned'].join(' \u00b7 ')));
            if (item.areaPath) card.append(node('div', 'ws-meta', item.areaPath));
            elements.wsList.append(card);
        });
    }

    function renderConversations() {
        const focusedId = elements.conversationList.contains(document.activeElement)
            ? document.activeElement.dataset.conversationId : null;
        elements.conversationList.replaceChildren();
        if (!state.conversations.length) {
            elements.conversationList.append(node('p', 'subtle', state.authenticated
                ? 'No saved conversations yet. Start a new one below.'
                : 'Sign in to see your saved conversations.'));
        }
        state.conversations.forEach(conversation => {
            const button = node('button', 'conversation');
            button.type = 'button';
            button.dataset.conversationId = conversation.id;
            if (conversation.id === state.currentId) button.setAttribute('aria-current', 'true');
            button.append(node('span', 'conversation-title', conversation.title || 'New conversation'));
            button.append(node('span', 'conversation-meta', [asText(conversation.model), dateLabel(conversation.updatedAt || conversation.createdAt)].filter(Boolean).join(' \u00b7 ')));
            button.addEventListener('click', () => openConversation(conversation.id));
            elements.conversationList.append(button);
            if (focusedId === conversation.id) button.focus({ preventScroll: true });
        });
    }

    async function refreshConversations() {
        return loadConversationPage(true);
    }

    async function loadOlderConversations() {
        return loadConversationPage(false);
    }

    async function loadConversationPage(reset) {
        if (!state.authenticated || (!reset && (state.listLoading || !state.listHasMore))) return;
        const version = ++state.listVersion;
        if (state.listController) state.listController.abort();
        const controller = new AbortController();
        state.listController = controller;
        const offset = reset ? 0 : state.listOffset;
        state.listLoading = true;
        state.listMode = reset ? 'refresh' : 'older';
        if (reset) {
            state.listOffset = 0;
            state.listHasMore = false;
        }
        elements.conversationList.setAttribute('aria-busy', 'true');
        updateControls();
        try {
            const data = await getJson('/api/agent/conversations?offset=' + offset + '&limit=' + conversationPageSize, controller.signal);
            if (version !== state.listVersion || !state.authenticated) return;
            if (!Array.isArray(data)) throw new Error('The conversation list had an unexpected format. Try Refresh again.');
            const merged = new Map((reset ? [] : state.conversations).map(item => [item.id, item]));
            data.filter(item => item && typeof item.id === 'string' && item.id)
                .forEach(item => merged.set(item.id, item));
            state.conversations = Array.from(merged.values())
                .sort((a, b) => (Date.parse(b.updatedAt || b.createdAt) || 0) - (Date.parse(a.updatedAt || a.createdAt) || 0));
            // Offset tracks server rows, not the deduplicated list, so overlapping pages still advance.
            state.listOffset = offset + data.length;
            state.listHasMore = data.length === conversationPageSize;
            renderConversations();
            const current = state.conversations.find(item => item.id === state.currentId);
            if (current && !state.loading) elements.conversationTitle.textContent = current.title || 'New conversation';
        } catch (error) {
            if (version === state.listVersion && error.name !== 'AbortError') showFailure(error);
        } finally {
            if (version === state.listVersion) {
                const loadFocused = document.activeElement === elements.loadOlderConversations;
                state.listLoading = false;
                state.listMode = null;
                elements.conversationList.setAttribute('aria-busy', 'false');
                updateControls();
                if (loadFocused && elements.loadOlderConversations.hidden) elements.refreshConversations.focus({ preventScroll: true });
            }
        }
    }

    function withScroll(action, force) {
        const transcript = elements.transcript;
        const follow = force || transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight < 100;
        const result = action();
        if (follow) transcript.scrollTop = transcript.scrollHeight;
        return result;
    }

    function showEmpty() {
        elements.transcript.replaceChildren();
        const empty = node('div', 'empty');
        empty.append(node('div', 'eyebrow', 'Your read-only research assistant'));
        empty.append(node('h3', '', 'What should we investigate?'));
        empty.append(node('p', '', 'Explore work items and GitHub issues, summarize feedback, or investigate the items you have pinned. Conversations are saved for you to resume later.'));
        const examples = node('div', 'examples');
        [
            'Summarize my pinned items and highlight common themes.',
            'Find open feedback bugs about debug performance and summarize the top three.'
        ].forEach(text => {
            const button = node('button', 'button example', text);
            button.type = 'button';
            button.addEventListener('click', () => {
                if (state.loading || state.turn) return;
                elements.prompt.value = text;
                updateControls();
                if (!elements.prompt.disabled) elements.prompt.focus();
            });
            examples.append(button);
        });
        empty.append(examples);
        elements.transcript.append(empty);
    }

    function appendMessage(role, content, createdAt, before) {
        return withScroll(() => {
            const empty = elements.transcript.querySelector('.empty');
            if (empty) empty.remove();
            const article = node('article', 'message ' + role);
            const header = node('div', 'message-header');
            header.append(node('span', 'message-role', role === 'user' ? 'You' : role === 'assistant' ? 'Agent' : role === 'warning' ? 'Warning' : 'Error'));
            const timestamp = dateLabel(createdAt);
            if (timestamp) header.append(node('span', 'message-time', timestamp));
            const label = node('span', 'message-state');
            header.append(label);
            const body = node('p', 'message-content', content);
            article.append(header, body);
            elements.transcript.insertBefore(article, before || null);
            return { article, body, label };
        });
    }

    function prettyText(value) {
        if (typeof value !== 'string') return value === undefined ? '' : JSON.stringify(value, null, 2);
        try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; }
    }

    function toolHasError(value) {
        try {
            let parsed = value;
            if (typeof value === 'string') {
                try { parsed = JSON.parse(value); }
                catch {
                    const savedResult = value.match(/\nResult:\s*([\s\S]*)$/);
                    if (!savedResult) return false;
                    parsed = JSON.parse(savedResult[1]);
                }
            }
            return !!(parsed && typeof parsed === 'object' && Object.prototype.hasOwnProperty.call(parsed, 'error') && parsed.error !== null);
        } catch { return false; }
    }

    function appendTool(name, args, result, before) {
        return withScroll(() => {
            const article = node('article', 'message tool');
            const empty = elements.transcript.querySelector('.empty');
            if (empty) empty.remove();
            const details = node('details');
            const summary = node('summary', '', asText(name) || 'Tool');
            const argHeading = node('h4', '', 'Arguments');
            const argumentsText = node('pre', '', prettyText(args));
            argHeading.hidden = argumentsText.hidden = args === null || args === undefined;
            const resultHeading = node('h4', '', 'Result');
            const resultText = node('pre');
            const errorNotice = node('p', 'tool-error', 'This tool reported an error. Review the result below; it is not a successful tool outcome.');
            errorNotice.hidden = true;
            details.append(summary, argHeading, argumentsText, errorNotice, resultHeading, resultText);
            article.append(details);
            elements.transcript.insertBefore(article, before || null);
            const tool = { name: asText(name) || 'Tool', article, details, summary, resultHeading, resultText, errorNotice, pending: true };
            if (result === undefined) {
                summary.textContent = tool.name + ' \u2014 running';
                resultHeading.hidden = resultText.hidden = true;
            } else {
                setToolResult(tool, result);
            }
            return tool;
        });
    }

    function setToolResult(tool, result) {
        withScroll(() => {
            const failed = toolHasError(result);
            tool.pending = false;
            tool.article.classList.toggle('has-error', failed);
            tool.summary.textContent = tool.name + (failed ? ' \u2014 reported an error' : ' \u2014 result received');
            tool.resultHeading.hidden = tool.resultText.hidden = false;
            tool.resultText.textContent = prettyText(result) || '(Empty tool result)';
            tool.errorNotice.hidden = !failed;
            if (failed) tool.details.open = true;
        });
    }

    function renderDetail(detail) {
        if (!detail || typeof detail.id !== 'string' || !Array.isArray(detail.messages)) {
            throw new Error('The saved conversation had an unexpected format. Try Reload again.');
        }
        state.messages = detail.messages;
        elements.conversationTitle.textContent = detail.title || 'New conversation';
        elements.conversationMeta.textContent = [asText(detail.model), 'Saved conversation \u00b7 Read-only'].filter(Boolean).join(' \u00b7 ');
        showEmpty();
        if (detail.summary) appendMessage('warning', 'Earlier context summary:\n' + asText(detail.summary));
        detail.messages.forEach(message => {
            if (!message || !['user', 'assistant', 'tool'].includes(message.role)) return;
            if (message.role === 'tool') appendTool('Saved tool result', null, message.content);
            else {
                const entry = appendMessage(message.role, message.content, message.createdAt);
                if (message.role === 'assistant' && /^\[Turn (failed|cancelled|canceled)\]/i.test(asText(message.content))) {
                    const failed = /^\[Turn failed\]/i.test(message.content);
                    entry.label.textContent = failed ? 'Previous turn failed' : 'Previous turn canceled';
                    entry.article.classList.toggle(failed ? 'error' : 'warning', true);
                }
            }
        });
        elements.transcript.scrollTop = elements.transcript.scrollHeight;
    }

    function beginView() {
        if (state.viewController) state.viewController.abort();
        if (state.recoveryController) state.recoveryController.abort();
        if (state.turn) {
            state.turn.cancelled = true;
            state.turn.controller.abort();
        }
        const version = ++state.viewVersion;
        state.viewController = new AbortController();
        state.loading = true;
        elements.prompt.value = '';
        updateControls();
        return { version, controller: state.viewController };
    }

    async function openConversation(id) {
        const view = beginView();
        state.currentId = id;
        state.messages = [];
        renderConversations();
        const summary = state.conversations.find(item => item.id === id);
        elements.conversationTitle.textContent = summary && summary.title || 'Saved conversation';
        elements.conversationMeta.textContent = 'Retrieving your saved messages.';
        elements.transcript.replaceChildren();
        showStatus('Loading conversation\u2026');
        try {
            const detail = await getJson('/api/agent/conversations/' + encodeURIComponent(id), view.controller.signal);
            if (view.version !== state.viewVersion) return;
            if (detail.id !== id) throw new Error('The server returned a different conversation. Refresh the conversation list and try again.');
            renderDetail(detail);
            showStatus('Conversation loaded. Continue with a new message; every turn is read-only.');
        } catch (error) {
            if (view.version === state.viewVersion && error.name !== 'AbortError') {
                elements.conversationMeta.textContent = 'Saved messages could not be loaded. Use Reload to try again.';
                showFailure(error);
            }
        } finally {
            if (view.version === state.viewVersion) {
                state.loading = false;
                updateControls();
            }
        }
    }

    async function createConversation(signal) {
        const response = await post('/api/agent/conversations', {}, signal);
        const summary = await response.json();
        if (!summary || typeof summary.id !== 'string' || !summary.id) {
            throw new Error('The new conversation had an unexpected format. Refresh the list before trying again.');
        }
        return summary;
    }

    function selectCreated(summary) {
        state.currentId = summary.id;
        state.messages = [];
        elements.conversationTitle.textContent = summary.title || 'New conversation';
        elements.conversationMeta.textContent = [asText(summary.model), 'Read-only \u00b7 Messages are saved to this conversation'].filter(Boolean).join(' \u00b7 ');
        state.conversations = [summary, ...state.conversations.filter(item => item.id !== summary.id)];
        renderConversations();
        showEmpty();
    }

    async function newConversation() {
        const view = beginView();
        state.currentId = null;
        state.messages = [];
        renderConversations();
        showEmpty();
        elements.conversationTitle.textContent = 'Creating conversation\u2026';
        showStatus('Creating your conversation\u2026');
        try {
            const summary = await createConversation(view.controller.signal);
            if (view.version !== state.viewVersion) return;
            selectCreated(summary);
            showStatus('New conversation ready. No work items, comments, or issues will be changed.');
        } catch (error) {
            if (view.version === state.viewVersion && error.name !== 'AbortError') {
                elements.conversationTitle.textContent = 'New conversation';
                elements.conversationMeta.textContent = 'The conversation could not be created. Refresh the list before trying again.';
                showFailure(error);
            }
        } finally {
            if (view.version === state.viewVersion) {
                state.loading = false;
                updateControls();
                if (!elements.prompt.disabled) elements.prompt.focus();
                await refreshConversations();
            }
        }
    }

    function currentTurn(turn) {
        return state.turn === turn && turn.version === state.viewVersion && state.currentId === turn.id;
    }

    function handleEvent(turn, event) {
        if (!currentTurn(turn)) return;
        switch (event.kind) {
            case 'turnStarted':
                turn.started = true;
                turn.assistant.label.textContent = 'Thinking\u2026';
                showStatus('The agent is investigating. You can cancel the response at any time.');
                break;
            case 'textDelta':
                turn.text += event.content || '';
                withScroll(() => {
                    turn.assistant.body.textContent = turn.text;
                    turn.assistant.label.textContent = 'Streaming\u2026';
                });
                break;
            case 'toolStarted': {
                const tool = appendTool(event.toolName, event.toolArguments, undefined, turn.assistant.article);
                turn.tools.push(tool);
                break;
            }
            case 'toolCompleted': {
                let tool = turn.tools.find(item => item.pending && item.name === (asText(event.toolName) || 'Tool'));
                if (!tool) {
                    tool = appendTool(event.toolName, event.toolArguments, undefined, turn.assistant.article);
                    turn.tools.push(tool);
                }
                setToolResult(tool, event.content);
                if (toolHasError(event.content)) turn.toolErrors = true;
                break;
            }
            case 'warning':
                turn.warnings = true;
                appendMessage('warning', event.content || 'The agent reported a warning.', null, turn.assistant.article);
                break;
            case 'error':
                turn.errorMessage = event.content || 'The agent stopped with an error.';
                appendMessage('error', turn.errorMessage);
                break;
            case 'completed':
                withScroll(() => {
                    // The terminal text replaces deltas; appending it would duplicate the answer.
                    if (event.content) turn.assistant.body.textContent = event.content;
                    else if (!turn.text) turn.assistant.body.textContent = '(No assistant text returned.)';
                    turn.assistant.label.textContent = 'Response received';
                });
                break;
        }
    }

    async function consumeStream(response, signal, onEvent) {
        if ((response.headers.get('Content-Type') || '').toLowerCase().split(';')[0].trim() !== 'application/x-ndjson') {
            throw new Error('The server did not return a conversation stream. Reload and try again.');
        }
        if (!response.body || !response.body.getReader) {
            throw new Error('This browser cannot read streaming responses. Use a current version of Microsoft Edge.');
        }
        const reader = response.body.getReader();
        const decoder = new TextDecoder('utf-8', { fatal: true });
        let buffer = '';
        let terminal = null;
        let ended = false;
        function parseLine(line) {
            if (!line.trim()) return;
            if (line.length > 4 * 1024 * 1024) throw new Error('A stream event was too large to display safely. Use Reload to check saved messages.');
            let event;
            try { event = JSON.parse(line); }
            catch { throw new Error('The response stream contained malformed or truncated JSON. Partial text is not a completed answer. Use Reload to check saved messages.'); }
            if (!event || !eventKinds.has(event.kind) || (event.content !== null && event.content !== undefined && typeof event.content !== 'string')) {
                throw new Error('The response stream contained an invalid event. Use Reload to check saved messages.');
            }
            onEvent(event);
            if (event.kind === 'completed' || event.kind === 'error') terminal = event.kind;
        }
        try {
            while (!terminal) {
                if (signal.aborted) throw new DOMException('Response canceled', 'AbortError');
                const chunk = await reader.read();
                if (signal.aborted) throw new DOMException('Response canceled', 'AbortError');
                ended = chunk.done;
                try { buffer += chunk.done ? decoder.decode() : decoder.decode(chunk.value, { stream: true }); }
                catch { throw new Error('The response stream contained incomplete or invalid UTF-8 text. Use Reload to check saved messages.'); }
                let newline;
                while (!terminal && (newline = buffer.indexOf('\n')) !== -1) {
                    const line = buffer.slice(0, newline);
                    buffer = buffer.slice(newline + 1);
                    parseLine(line);
                }
                if (buffer.length > 4 * 1024 * 1024) throw new Error('A stream event was too large to display safely. Use Reload to check saved messages.');
                if (chunk.done) {
                    if (!terminal && buffer.trim()) parseLine(buffer);
                    break;
                }
            }
            if (!terminal) throw new Error('The response ended before a completed or error event. Partial text is not a completed answer. Use Reload to check saved messages.');
            return terminal;
        } finally {
            if (!ended) {
                try { await reader.cancel(); } catch { /* Aborting fetch may already have closed the reader. */ }
            }
            reader.releaseLock();
        }
    }

    function markUnfinished(turn, label) {
        turn.assistant.label.textContent = label;
        if (!turn.assistant.body.textContent) turn.assistant.body.textContent = 'No completed answer was received.';
        turn.tools.filter(tool => tool.pending).forEach(tool => {
            tool.summary.textContent = tool.name + ' \u2014 interrupted; outcome unknown';
            tool.pending = false;
        });
    }

    async function sendPrompt(event) {
        if (event) event.preventDefault();
        const text = elements.prompt.value.trim();
        if (!text || !state.authenticated || state.loading || state.turn) return;
        if (text.length > 16000) {
            showStatus('Messages are limited to 16,000 characters. Shorten the message before sending.', 'warning');
            return;
        }
        const workingSet = readWorkingSet();
        if (workingSet.length > 50) {
            showStatus('The working set is limited to 50 items per message. Remove some pinned items before sending.', 'warning');
            return;
        }
        if (state.recoveryController) state.recoveryController.abort();
        const turn = {
            id: state.currentId,
            version: state.viewVersion,
            controller: new AbortController(),
            cancelled: false,
            started: false,
            terminal: null,
            text: '',
            tools: [],
            toolErrors: false,
            warnings: false,
            baseline: state.messages.length,
            workingSet,
            requestAttempted: false
        };
        state.turn = turn;
        updateControls();
        showStatus('Preparing your read-only request\u2026');
        try {
            if (!turn.id) {
                const summary = await createConversation(turn.controller.signal);
                if (!currentTurn(turn)) return;
                selectCreated(summary);
                turn.id = summary.id;
                turn.baseline = 0;
            }
            if (!currentTurn(turn)) return;
            const saved = await getJson('/api/agent/conversations/' + encodeURIComponent(turn.id), turn.controller.signal);
            if (!currentTurn(turn)) return;
            if (!saved || saved.id !== turn.id || !Array.isArray(saved.messages)) {
                throw new Error('Saved messages could not be checked. Use Reload before sending again.');
            }
            turn.baseline = saved.messages.length;
            turn.baselineTime = saved.messages.reduce((latest, message) => Math.max(latest, Date.parse(message.createdAt) || 0), 0);
            state.messages = saved.messages;
            withScroll(() => {
                turn.user = appendMessage('user', text, new Date().toISOString());
                turn.assistant = appendMessage('assistant', '');
                turn.assistant.label.textContent = 'Connecting\u2026';
            }, true);
            elements.prompt.value = '';
            turn.requestAttempted = true;
            const response = await post('/api/agent/conversations/' + encodeURIComponent(turn.id) + '/stream', {
                prompt: text,
                workingSet: turn.workingSet,
                dryRun: true
            }, turn.controller.signal, 'application/x-ndjson');
            turn.terminal = await consumeStream(response, turn.controller.signal, data => handleEvent(turn, data));
            if (!currentTurn(turn)) return;
            if (turn.terminal === 'error') {
                markUnfinished(turn, 'Incomplete \u00b7 Agent error');
                showStatus(turn.errorMessage + ' Use Reload to check saved messages.', 'error');
            } else {
                turn.tools.filter(tool => tool.pending).forEach(tool => {
                    tool.summary.textContent = tool.name + ' \u2014 no result received';
                    turn.warnings = true;
                });
                showStatus(turn.toolErrors
                    ? 'Response received, but a tool reported an error. Review the expanded tool details.'
                    : turn.warnings ? 'Response received with warnings. Review the details above.'
                        : 'Response received. Send another message to continue this read-only conversation.',
                turn.toolErrors || turn.warnings ? 'warning' : '');
                // Keep a local baseline until the next saved-conversation reload.
                state.messages.push({ role: 'user', content: text }, { role: 'assistant', content: turn.assistant.body.textContent });
            }
        } catch (error) {
            if (!currentTurn(turn)) return;
            if (turn.assistant) markUnfinished(turn, turn.cancelled ? 'Canceled \u00b7 Partial response' : 'Incomplete');
            if (turn.cancelled || error.name === 'AbortError') {
                showStatus('Response canceled. Partial text is not a completed answer. Checking for saved messages while the server stops\u2026', 'warning');
            } else {
                // Keep the prompt available when HTTP/authentication fails before the turn starts.
                if (!turn.started) {
                    elements.prompt.value = text;
                    if (turn.user && error instanceof HttpError) turn.user.label.textContent = 'Request rejected \u00b7 Not sent';
                }
                showFailure(error);
            }
        } finally {
            if (state.turn === turn) {
                state.turn = null;
                updateControls();
                if (document.activeElement === elements.cancelBtn && !elements.prompt.disabled) elements.prompt.focus();
            }
            if (turn.version === state.viewVersion && turn.cancelled && turn.id && turn.requestAttempted && state.authenticated) {
                await recoverCanceled(turn);
            } else if (turn.version === state.viewVersion && turn.cancelled) {
                showStatus('Request canceled before a response was received. You can send another message when ready.', 'warning');
            }
            await refreshConversations();
        }
    }

    function waitForRecovery(milliseconds, signal) {
        return new Promise((resolve, reject) => {
            if (signal.aborted) { reject(new DOMException('Recovery canceled', 'AbortError')); return; }
            const abort = () => {
                clearTimeout(timer);
                reject(new DOMException('Recovery canceled', 'AbortError'));
            };
            const timer = setTimeout(() => {
                signal.removeEventListener('abort', abort);
                resolve();
            }, milliseconds);
            signal.addEventListener('abort', abort, { once: true });
        });
    }

    async function recoverCanceled(turn) {
        const controller = new AbortController();
        state.recoveryController = controller;
        const stillCurrent = () => !controller.signal.aborted && turn.version === state.viewVersion && state.currentId === turn.id && !state.turn;
        try {
            // Disconnect cancellation can race with the server persisting its final message.
            for (const delay of [500, 1000, 2000, 3000]) {
                await waitForRecovery(delay, controller.signal);
                const detail = await getJson('/api/agent/conversations/' + encodeURIComponent(turn.id), controller.signal);
                if (!stillCurrent()) return;
                if (!detail || detail.id !== turn.id || !Array.isArray(detail.messages)) throw new Error('Saved messages could not be checked. Use Reload to try again.');
                const appended = turn.baselineTime
                    ? detail.messages.filter(message => (Date.parse(message.createdAt) || 0) > turn.baselineTime)
                    : detail.messages.slice(turn.baseline);
                if (appended.some(message => message.role === 'assistant')) {
                    renderDetail(detail);
                    showStatus('Request canceled. Saved messages have been reloaded; a cancellation notice is not a completed answer. You can continue with a new message.', 'warning');
                    return;
                }
            }
            if (stillCurrent()) {
                showStatus('Response canceled. Partial text may not be saved. The server may still be stopping; use Reload to check saved messages. If a turn is still in progress, wait and reload again.', 'warning');
            }
        } catch (error) {
            if (stillCurrent() && error.name !== 'AbortError') showFailure(error);
        } finally {
            if (state.recoveryController === controller) state.recoveryController = null;
        }
    }

    elements.newConversation.addEventListener('click', newConversation);
    elements.refreshConversations.addEventListener('click', refreshConversations);
    elements.loadOlderConversations.addEventListener('click', loadOlderConversations);
    elements.reloadConversation.addEventListener('click', () => {
        if (state.currentId) openConversation(state.currentId);
    });
    elements.composer.addEventListener('submit', sendPrompt);
    elements.prompt.addEventListener('input', updateControls);
    elements.prompt.addEventListener('keydown', event => {
        if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) sendPrompt(event);
    });
    elements.cancelBtn.addEventListener('click', () => {
        if (!state.turn) return;
        state.turn.cancelled = true;
        state.turn.controller.abort();
        showStatus('Canceling the response. The server may need a moment to stop\u2026', 'warning');
        updateControls();
    });
    elements.statusSignIn.addEventListener('click', async () => {
        elements.statusSignIn.disabled = true;
        try {
            if (await signInWithPopup()) window.location.reload();
        } catch (error) {
            showStatus('Sign-in could not be opened. Allow popups for this site and try again. ' + error.message, 'error', true);
        } finally { elements.statusSignIn.disabled = false; }
    });
    elements.wsRefresh.addEventListener('click', renderWorkingSet);
    elements.wsClear.addEventListener('click', () => {
        try {
            AgentWorkingSet.clear();
            renderWorkingSet();
        } catch { showStatus('The local working set could not be cleared. Check browser storage permissions.', 'error'); }
    });
    window.addEventListener('storage', event => {
        if (event.key === AgentWorkingSet.KEY || event.key === null) renderWorkingSet();
    });

    async function init() {
        renderWorkingSet();
        showEmpty();
        renderUserInfo('userInfo', { linkColor: '#ffffff' });
        try {
            await checkResponse(await authFetch('/api/me', { cache: 'no-store' }));
            state.authenticated = true;
            showStatus('Ready when you are. Start a new conversation or resume one from the sidebar.');
            updateControls();
            await refreshConversations();
        } catch (error) { showFailure(error); }
    }

    init();
})();
