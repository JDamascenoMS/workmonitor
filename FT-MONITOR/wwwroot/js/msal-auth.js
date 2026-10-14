(function () {
    'use strict';

    const callbackPath = '/auth-callback.html';
    const sdkPath = '/vendor/msal-browser/5.25.0/msal-browser.min.js';
    const sdkIntegrity = 'sha256-DnUo1gmU2EzBQ5cRcsQaUpQnL6jx74554sMX3NCxyf8=';
    const localMode = 'localDevelopment';
    const localPopupPath = '/signin-popup-complete.html';
    const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
    const devOpsGetRoutes = /^\/api\/(?:listar|top-bugs|top-feedback|assignees|search-mentions|feedback-themes|most-relevant-tickets|work-item-types|my-dashboard|my-items|(?:work-item-details|summarize|draft-response|bug-probability|execute-query)\/[^/]+)\/?$/i;
    const renderVersions = new WeakMap();
    let sdkPromise;
    let initializationPromise;
    let interactionPromise;

    class AuthActionError extends Error {
        constructor(message, errorCode, status) {
            super(message);
            this.name = 'AuthActionError';
            this.errorCode = errorCode;
            this.status = status || 401;
        }
    }

    function isRecord(value) {
        return value !== null && typeof value === 'object' && !Array.isArray(value);
    }

    function developerIdentityLabel(identity) {
        if (typeof identity === 'string') return identity.trim();
        if (!isRecord(identity)) return '';
        function field(names) {
            for (const name of names) {
                if (typeof identity[name] === 'string' && identity[name].trim()) return identity[name].trim();
            }
            return '';
        }
        const name = field(['displayName', 'name']);
        const email = field(['principalName', 'email', 'username', 'userName']);
        if (name || email) return name && email && name !== email ? name + ' (' + email + ')' : name || email;
        const objectId = field(['originId', 'objectId', 'oid']);
        const tenantId = field(['domain', 'tenantId', 'tid']);
        return objectId ? 'Object ID: ' + objectId + (tenantId ? '; tenant: ' + tenantId : '') : '';
    }

    function validUser(body, context) {
        return isRecord(body) && typeof body.name === 'string' && typeof body.email === 'string' &&
            (context.status.mode !== localMode ||
                (body.authMode === localMode && !!developerIdentityLabel(body.devOpsIdentity)));
    }

    function abortReason(signal) {
        return signal.reason === undefined
            ? new DOMException('The request was aborted.', 'AbortError')
            : signal.reason;
    }

    function checkAbort(signal) {
        if (signal && signal.aborted) throw abortReason(signal);
    }

    // MSAL cannot cancel an in-flight acquisition. Stop this caller immediately,
    // and never send its request or begin another acquisition after cancellation.
    function cancellable(promise, signal) {
        if (signal && signal.aborted) Promise.resolve(promise).catch(function () {});
        checkAbort(signal);
        if (!signal) return promise;
        return new Promise(function (resolve, reject) {
            function cleanup() { signal.removeEventListener('abort', onAbort); }
            function onAbort() {
                cleanup();
                reject(abortReason(signal));
            }
            signal.addEventListener('abort', onAbort, { once: true });
            Promise.resolve(promise).then(function (value) {
                cleanup();
                if (signal.aborted) reject(abortReason(signal));
                else resolve(value);
            }, function (error) {
                cleanup();
                reject(signal.aborted ? abortReason(signal) : error);
            });
            if (signal.aborted) onAbort();
        });
    }

    function loadSdk() {
        if (!sdkPromise) {
            sdkPromise = new Promise(function (resolve, reject) {
                if (window.msal) {
                    resolve(window.msal);
                    return;
                }
                const script = document.createElement('script');
                script.src = sdkPath;
                script.integrity = sdkIntegrity;
                script.onload = function () {
                    if (window.msal) resolve(window.msal);
                    else reject(new AuthActionError('The Microsoft sign-in library did not load. Reload this page.', 'sdk_unavailable', 503));
                };
                script.onerror = function () {
                    reject(new AuthActionError('The Microsoft sign-in library could not load. Reload this page.', 'sdk_unavailable', 503));
                };
                document.head.append(script);
            });
        }
        return sdkPromise;
    }

    function requiresInteraction(error) {
        return !!error && (
            (window.msal && error instanceof window.msal.InteractionRequiredAuthError) ||
            ['interaction_required', 'login_required', 'consent_required', 'no_tokens_found', 'no_account_error'].includes(error.errorCode) ||
            ['consent_required', 'basic_action', 'additional_action'].includes(error.subError)
        );
    }

    function actionable(error, resource) {
        if (error instanceof AuthActionError) return error;
        if (requiresInteraction(error)) {
            return new AuthActionError(
                'Select Sign in to renew your session or grant consent for ' + resource + '. No sign-in window opens automatically.',
                'interaction_required'
            );
        }
        const code = error && /^[a-z0-9_.-]{1,80}$/i.test(error.errorCode || '') ? error.errorCode : 'sign_in_failed';
        return new AuthActionError(
            'Microsoft sign-in failed (' + code + '). Check the Entra SPA redirect URI, the exposed access_as_user API scope, and delegated Azure DevOps consent with your administrator.',
            code
        );
    }

    function initialize() {
        if (!initializationPromise) {
            initializationPromise = (async function () {
                let response;
                try {
                    response = await fetch('/api/auth/status', { credentials: 'omit', cache: 'no-store', redirect: 'error' });
                } catch {
                    throw new AuthActionError('Could not check sign-in configuration. Reload this page when the server is available.', 'auth_status_unavailable', 503);
                }
                if (!response.ok) {
                    throw new AuthActionError('Could not check sign-in configuration. Ask your administrator to check /api/auth/status.', 'auth_status_unavailable', 503);
                }
                let status;
                try {
                    status = await response.json();
                } catch {
                    throw new AuthActionError('The server returned invalid sign-in configuration. Ask your administrator to check /api/auth/status.', 'invalid_auth_configuration', 503);
                }
                if (!isRecord(status) || typeof status.configured !== 'boolean') {
                    throw new AuthActionError('The server returned invalid sign-in configuration. Ask your administrator to check /api/auth/status.', 'invalid_auth_configuration', 503);
                }
                if (!status.configured) {
                    const message = typeof status.error === 'string' && status.error
                        ? status.error : 'Browser sign-in is not configured. Ask your administrator to configure the Entra application.';
                    throw new AuthActionError(message, 'auth_not_configured', 503);
                }
                if (status.mode === localMode) {
                    const location = new URL(window.location.href);
                    if (!['localhost', '127.0.0.1', '[::1]'].includes(location.hostname.toLowerCase()) ||
                        !['http:', 'https:'].includes(location.protocol) ||
                        typeof status.clientId !== 'string' || !status.clientId.trim() ||
                        typeof status.authority !== 'string' || !status.authority.trim() ||
                        status.apiScope !== null || status.devOpsScope !== null ||
                        status.redirectPath !== '/signin-oidc' ||
                        status.signInPath !== '/MicrosoftIdentity/Account/SignIn' ||
                        status.csrfPath !== '/api/agent/csrf' ||
                        status.signOutPath !== '/api/auth/local-sign-out' ||
                        typeof status.warning !== 'string' || !status.warning.trim()) {
                        throw new AuthActionError('Local development sign-in requires explicit localhost-only server configuration, basic OIDC sign-in, and CSRF protection. SPA remains the default.', 'invalid_auth_configuration', 503);
                    }
                    return { client: null, status };
                }
                if (status.mode !== 'spaPkce' ||
                    typeof status.clientId !== 'string' || !status.clientId.trim() ||
                    typeof status.authority !== 'string' || !status.authority.trim() ||
                    status.apiScope !== 'api://' + status.clientId + '/access_as_user' ||
                    status.devOpsScope !== '499b84ac-1321-427f-aa17-267ca6975798/user_impersonation' ||
                    status.redirectPath !== callbackPath) {
                    throw new AuthActionError('The server must provide SPA + PKCE configuration with separate WorkMonitor and Azure DevOps scopes and /auth-callback.html.', 'invalid_auth_configuration', 503);
                }
                const sdk = await loadSdk();
                let client;
                try {
                    client = new sdk.PublicClientApplication({
                        auth: {
                            clientId: status.clientId,
                            authority: status.authority,
                            redirectUri: window.location.origin + callbackPath,
                            postLogoutRedirectUri: window.location.origin + callbackPath
                        },
                        cache: { cacheLocation: 'sessionStorage' },
                        system: { loggerOptions: { piiLoggingEnabled: false, loggerCallback: function () {} } }
                    });
                    await client.initialize();
                    // MSAL v5's callback bridge returns to the initiating page.
                    // Process its cached redirect response before any API request.
                    const result = await client.handleRedirectPromise();
                    if (result && result.account) client.setActiveAccount(result.account);
                } catch (error) {
                    throw actionable(error, 'WorkMonitor');
                }
                return { client, status };
            })();
        }
        return initializationPromise;
    }

    function sameAccount(left, right) {
        return !!left && !!right && !!left.homeAccountId &&
            left.homeAccountId === right.homeAccountId &&
            left.localAccountId === right.localAccountId &&
            left.tenantId === right.tenantId;
    }

    function checkAccount(client, account, result) {
        if (!sameAccount(client.getActiveAccount(), account) ||
            (result && !sameAccount(result.account, account))) {
            throw new AuthActionError('The selected account changed. Select Sign in, then retry the request.', 'account_changed');
        }
    }

    async function acquire(context, account, scope, resource, signal) {
        checkAbort(signal);
        checkAccount(context.client, account);
        let result;
        try {
            result = await cancellable(context.client.acquireTokenSilent({
                scopes: [scope],
                account,
                redirectUri: window.location.origin + callbackPath
            }), signal);
        } catch (error) {
            checkAbort(signal);
            throw actionable(error, resource);
        }
        checkAbort(signal);
        checkAccount(context.client, account, result);
        if (!result || typeof result.accessToken !== 'string' || !result.accessToken) {
            throw new AuthActionError('No access token was returned for ' + resource + '. Check delegated scopes and consent with your administrator.', 'access_token_missing');
        }
        return result.accessToken;
    }

    function needsDevOps(path, method) {
        return (method === 'GET' && devOpsGetRoutes.test(path)) ||
            (method === 'POST' && (/^\/api\/agent\/run\/?$/i.test(path) ||
                /^\/api\/agent\/conversations\/[^/]+\/stream\/?$/i.test(path)));
    }

    function authFailure(error) {
        return new Response(JSON.stringify({ error: error.message, errorCode: error.errorCode }), {
            status: error.status,
            headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }
        });
    }

    async function localFetch(context, url, outgoing, method, signal) {
        outgoing.credentials = 'same-origin';
        outgoing.redirect = 'error';
        if (unsafeMethods.has(method)) {
            checkAbort(signal);
            const response = await cancellable(fetch(context.status.csrfPath, {
                credentials: 'same-origin',
                cache: 'no-store',
                redirect: 'error',
                signal
            }), signal);
            checkAbort(signal);
            if (!response.ok) return response;
            let csrf;
            try {
                csrf = await cancellable(response.json(), signal);
            } catch {
                checkAbort(signal);
                throw new AuthActionError('Could not obtain browser CSRF protection. Reload the page and sign in again.', 'csrf_unavailable', 503);
            }
            checkAbort(signal);
            if (!isRecord(csrf) || typeof csrf.requestToken !== 'string' || !csrf.requestToken.trim() ||
                /[\x00-\x1f\x7f]/.test(csrf.requestToken)) {
                throw new AuthActionError('The server returned invalid CSRF protection. Reload the page and sign in again.', 'csrf_unavailable', 503);
            }
            outgoing.headers.set('X-CSRF-TOKEN', csrf.requestToken);
        }
        checkAbort(signal);
        return fetch(url, outgoing);
    }

    async function authFetch(url, options) {
        options = options || {};
        const request = typeof Request !== 'undefined' && url instanceof Request ? url : null;
        const signal = options.signal === undefined && request ? request.signal : options.signal;
        checkAbort(signal);
        const destination = new URL(request ? request.url : String(url), window.location.href);
        const headers = new Headers(options.headers === undefined && request ? request.headers : options.headers);
        headers.delete('Authorization');
        headers.delete('X-WorkMonitor-DevOps-Token');
        headers.delete('X-CSRF-TOKEN');
        headers.delete('Cookie');
        headers.delete('Cookie2');
        const outgoing = Object.assign({}, options, { headers, credentials: 'omit' });
        if (signal) outgoing.signal = signal;
        const method = (options.method || (request && request.method) || 'GET').toUpperCase();
        const apiRequest = destination.origin === window.location.origin &&
            destination.pathname.toLowerCase().startsWith('/api/');
        const publicStatus = destination.pathname.toLowerCase() === '/api/auth/status';
        if (!apiRequest) return fetch(url, outgoing);
        if (destination.username || destination.password) throw new TypeError('API URLs must not contain credentials.');

        // Fetch would strip Authorization on a cross-origin redirect, but not
        // our DevOps header. Never follow redirects with either bearer token.
        outgoing.redirect = options.redirect === 'manual' ? 'manual' : 'error';
        if (publicStatus && !unsafeMethods.has(method)) return fetch(url, outgoing);
        try {
            const context = await cancellable(initialize(), signal);
            checkAbort(signal);
            if (context.status.mode === localMode) {
                return await localFetch(context, url, outgoing, method, signal);
            }
            if (publicStatus) return fetch(url, outgoing);
            const account = context.client.getActiveAccount();
            if (!account) {
                return authFailure(new AuthActionError('Select Sign in and choose your Microsoft account to use WorkMonitor.', 'sign_in_required'));
            }
            headers.set('Authorization', 'Bearer ' + await acquire(context, account, context.status.apiScope, 'WorkMonitor', signal));
            if (needsDevOps(destination.pathname, method)) {
                headers.set('X-WorkMonitor-DevOps-Token', await acquire(context, account, context.status.devOpsScope, 'Azure DevOps', signal));
            }
            checkAbort(signal);
            checkAccount(context.client, account);
        } catch (error) {
            checkAbort(signal);
            if (error instanceof AuthActionError) return authFailure(error);
            throw error;
        }
        return fetch(url, outgoing);
    }

    function popupBlocked(error) {
        return error && ['popup_window_error', 'empty_window_error'].includes(error.errorCode);
    }

    async function interactiveAcquire(context, account, scope, resource) {
        try {
            return await acquire(context, account, scope, resource);
        } catch (error) {
            if (error.errorCode !== 'interaction_required') throw error;
        }
        let result;
        const request = { scopes: [scope], account, redirectUri: window.location.origin + callbackPath };
        try {
            result = await context.client.acquireTokenPopup(request);
        } catch (error) {
            if (popupBlocked(error)) {
                await context.client.acquireTokenRedirect(Object.assign({}, request, { redirectStartPage: window.location.href }));
                return false;
            }
            throw error;
        }
        checkAccount(context.client, account, result);
        if (!result || !result.accessToken) {
            throw new AuthActionError('No access token was returned for ' + resource + '. Ask your administrator to check scopes and consent.', 'access_token_missing');
        }
        return result.accessToken;
    }

    async function performSignIn() {
        const context = await initialize();
        if (context.status.mode === localMode) return localSignIn(context);
        let account = context.client.getActiveAccount();
        try {
            if (!account) {
                const request = {
                    scopes: [context.status.apiScope],
                    extraScopesToConsent: [context.status.devOpsScope],
                    prompt: 'select_account',
                    redirectUri: window.location.origin + callbackPath
                };
                let result;
                try {
                    result = await context.client.loginPopup(request);
                } catch (error) {
                    if (popupBlocked(error)) {
                        await context.client.loginRedirect(Object.assign({}, request, { redirectStartPage: window.location.href }));
                        return false;
                    }
                    throw error;
                }
                if (!result || !result.account) throw new AuthActionError('No account was selected. Select Sign in and choose your account.', 'sign_in_required');
                account = result.account;
                context.client.setActiveAccount(account);
            }
            if (!await interactiveAcquire(context, account, context.status.apiScope, 'WorkMonitor')) return false;
            if (!await interactiveAcquire(context, account, context.status.devOpsScope, 'Azure DevOps')) return false;
            return true;
        } catch (error) {
            if (error && error.errorCode === 'user_cancelled') return false;
            throw actionable(error, 'WorkMonitor and Azure DevOps');
        }
    }

    function localSignInUrl(context, popup) {
        const location = new URL(window.location.href);
        const returnPath = location.pathname.startsWith('//') ? '/' : location.pathname + location.search + location.hash;
        return context.status.signInPath + '?redirectUri=' + encodeURIComponent(popup ? localPopupPath : returnPath);
    }

    function localSignIn(context) {
        let popup;
        try {
            popup = window.open(localSignInUrl(context, true),
                'workmonitor-local-signin', 'width=520,height=640,resizable=yes,scrollbars=yes');
        } catch {
            popup = null;
        }
        if (!popup) {
            window.location.assign(localSignInUrl(context, false));
            return Promise.resolve(false);
        }
        return new Promise(function (resolve, reject) {
            let settled = false;
            let verifying = false;
            const verification = new AbortController();
            function cleanup() {
                window.removeEventListener('message', onMessage);
                clearInterval(closedTimer);
                clearTimeout(timeout);
                verification.abort();
            }
            function finish(success, error) {
                if (settled) return;
                settled = true;
                cleanup();
                try { popup.close(); } catch {}
                if (error) reject(error);
                else resolve(success);
            }
            async function onMessage(event) {
                if (settled || verifying || event.origin !== window.location.origin || event.source !== popup ||
                    !isRecord(event.data) || event.data.type !== 'ft-monitor-auth' || event.data.status !== 'success') return;
                verifying = true;
                clearInterval(closedTimer);
                try {
                    const response = await authFetch('/api/me', { cache: 'no-store', signal: verification.signal });
                    if (!response.ok) {
                        throw new AuthActionError('Local development sign-in was not verified. Use the account matching the developer’s cached CLI/Azure DevOps identity, then retry.', 'local_sign_in_unverified');
                    }
                    const user = await response.json();
                    if (!validUser(user, context)) {
                        throw new AuthActionError('The server did not verify the local development identity. Ask the developer to check /api/me and their cached CLI/Azure DevOps account.', 'local_sign_in_unverified');
                    }
                    finish(true);
                } catch (error) {
                    finish(false, error instanceof AuthActionError ? error :
                        new AuthActionError('Could not verify local development sign-in. Reload the page and retry. The developer’s cached identity must match your browser account.', 'local_sign_in_unverified'));
                }
            }
            window.addEventListener('message', onMessage);
            const closedTimer = setInterval(function () {
                if (popup.closed && !verifying) finish(false);
            }, 500);
            const timeout = setTimeout(function () {
                finish(false, new AuthActionError('Local development sign-in timed out. Close the sign-in window and select Sign in again.', 'local_sign_in_timeout'));
            }, 300000);
        });
    }

    function signInWithPopup() {
        if (!interactionPromise) {
            interactionPromise = performSignIn().finally(function () { interactionPromise = null; });
        }
        return interactionPromise;
    }

    async function signOut() {
        if (interactionPromise) throw new AuthActionError('Finish the current sign-in before signing out.', 'interaction_in_progress');
        const context = await initialize();
        if (context.status.mode === localMode) {
            const response = await authFetch(context.status.signOutPath, { method: 'POST', cache: 'no-store' });
            if (!response.ok) {
                throw new AuthActionError('Could not sign out of local development mode. Reload the page and retry. The developer’s CLI credentials have not been changed.', 'local_sign_out_failed');
            }
            return;
        }
        const account = context.client.getActiveAccount();
        if (!account) return;
        context.client.setActiveAccount(null);
        const request = { account, postLogoutRedirectUri: window.location.origin + callbackPath };
        try {
            await context.client.logoutPopup(request);
        } catch (error) {
            if (popupBlocked(error)) await context.client.logoutRedirect(request);
            else throw actionable(error, 'WorkMonitor');
        }
    }

    async function renderUserInfo(elementId, options) {
        options = options || {};
        const element = document.getElementById(elementId);
        if (!element) return;
        const version = {};
        renderVersions.set(element, version);
        let user;
        let context;
        let errorMessage = '';
        try {
            context = await initialize();
            const response = await authFetch('/api/me');
            if (response.ok) {
                const body = await response.json();
                if (validUser(body, context)) {
                    user = body;
                } else {
                    errorMessage = 'The server returned invalid account information. Reload this page or ask your administrator to check /api/me.';
                }
            } else {
                const body = await response.json();
                errorMessage = isRecord(body) && typeof body.error === 'string' ? body.error : 'Select Sign in to continue.';
            }
        } catch (error) {
            errorMessage = error instanceof AuthActionError ? error.message :
                'Could not load your account. Check your connection and select Sign in to retry.';
        }
        if (renderVersions.get(element) !== version) return;
        element.replaceChildren();
        if (context && context.status.mode === localMode) {
            const warning = document.createElement('span');
            warning.setAttribute('role', 'note');
            warning.style.display = 'block';
            warning.style.fontWeight = '600';
            warning.textContent = '⚠ LOCAL DEVELOPMENT ONLY — localhost, read-only, developer cached CLI/Azure DevOps identity. Auth mode: localDevelopment. ' +
                context.status.warning + (user ? ' Developer cached identity: ' + developerIdentityLabel(user.devOpsIdentity) + '.' :
                    ' Sign in with the same account as the developer’s cached identity.');
            element.append(warning);
        }
        const link = document.createElement('a');
        link.href = '#';
        link.style.color = options.linkColor || '#0078d4';
        link.style.fontWeight = '600';
        const notice = document.createElement('span');
        notice.setAttribute('role', 'alert');
        if (user) {
            const name = document.createElement('strong');
            name.textContent = String(user.name || 'Signed-in user');
            element.append(document.createTextNode('👤 '), name);
            if (user.email) element.append(document.createTextNode(' (' + String(user.email) + ')'));
            element.append(document.createTextNode(' — '));
            link.textContent = 'Sign out';
        } else {
            element.append(document.createTextNode('🔐 Not signed in — '));
            link.id = elementId + '-signin';
            link.textContent = 'Sign in';
            notice.textContent = errorMessage ? ' ' + errorMessage : '';
        }
        element.append(link);
        let renewLink;
        if (user) {
            renewLink = document.createElement('a');
            renewLink.href = '#';
            renewLink.style.color = options.linkColor || '#0078d4';
            renewLink.style.fontWeight = '600';
            renewLink.textContent = 'Sign in again';
            element.append(document.createTextNode(' · '), renewLink);
        }
        if (context && context.status.mode === localMode) {
            const pageSignIn = document.createElement('a');
            pageSignIn.href = localSignInUrl(context, false);
            pageSignIn.style.color = options.linkColor || '#0078d4';
            pageSignIn.textContent = 'Sign in on this page';
            element.append(document.createTextNode(' · '), pageSignIn);
        }
        element.append(notice);
        let busy = false;
        async function handleAction(event, actionLink, signingOut) {
            event.preventDefault();
            if (busy) return;
            busy = true;
            notice.textContent = '';
            const label = actionLink.textContent;
            actionLink.textContent = signingOut ? 'Signing out…' : 'Signing in…';
            actionLink.setAttribute('aria-disabled', 'true');
            try {
                if (signingOut) {
                    await signOut();
                    window.location.reload();
                } else if (await signInWithPopup()) {
                    window.location.reload();
                }
            } catch (error) {
                notice.textContent = ' ' + (error.message || 'Sign-in failed. Please retry.');
            } finally {
                busy = false;
                actionLink.textContent = label;
                actionLink.removeAttribute('aria-disabled');
            }
        }
        link.addEventListener('click', function (event) {
            return handleAction(event, link, !!user);
        });
        if (renewLink) {
            renewLink.addEventListener('click', function (event) {
                return handleAction(event, renewLink, false);
            });
        }
    }

    window.authFetch = authFetch;
    window.signInWithPopup = signInWithPopup;
    window.renderUserInfo = renderUserInfo;
    initialize().catch(function () {});
})();
