// Server-side token acquisition (OBO / authorization code flow).
// The OIDC session cookie authenticates all API calls automatically.
// No client-side MSAL.js is needed — the server exchanges the auth code
// for an Azure DevOps token on behalf of the signed-in user.
//
// When the session is missing/expired, the server returns 401 for /api/*
// requests (configured in Program.cs). Pages should not auto-redirect on
// 401 — instead they render a "Sign in" link in the user-info slot via
// renderUserInfo(...). The link opens an OIDC popup; on success the page
// is reloaded so freshly cookied requests succeed.

// ---------------------------------------------------------------------------
// authFetch — wrapper around fetch() that always sends the auth cookie.
// On 401 it simply returns the response so callers can handle it (typically
// by showing the Sign in link via renderUserInfo).
// ---------------------------------------------------------------------------
async function authFetch(url, options) {
    options = options || {};
    if (!options.credentials) options.credentials = 'same-origin';
    return fetch(url, options);
}

// ---------------------------------------------------------------------------
// signInWithPopup — opens the Microsoft Identity Web sign-in endpoint in a
// popup window. Resolves true when the popup signals success (it lands on
// /signin-popup-complete.html which posts a message back). Resolves false
// if the popup is closed without completing.
// ---------------------------------------------------------------------------
function signInWithPopup() {
    return new Promise(function (resolve) {
        var redirectUri = '/signin-popup-complete.html';
        var url = '/MicrosoftIdentity/Account/SignIn?redirectUri=' + encodeURIComponent(redirectUri);

        var w = 520, h = 640;
        var left = window.screenX + Math.max(0, (window.outerWidth - w) / 2);
        var top  = window.screenY + Math.max(0, (window.outerHeight - h) / 2);
        var popup = window.open(url, 'ft-monitor-signin',
            'width=' + w + ',height=' + h + ',left=' + left + ',top=' + top +
            ',resizable=yes,scrollbars=yes,status=yes');

        if (!popup) {
            // Popup blocked — fall back to full-page navigation.
            window.location.href = url.replace(redirectUri, window.location.pathname + window.location.search);
            return;
        }

        var done = false;
        function finish(success) {
            if (done) return;
            done = true;
            window.removeEventListener('message', onMessage);
            clearInterval(closedTimer);
            resolve(success);
        }

        function onMessage(ev) {
            if (ev.origin !== window.location.origin) return;
            if (ev.data && ev.data.type === 'ft-monitor-auth' && ev.data.status === 'success') {
                try { popup.close(); } catch (e) {}
                finish(true);
            }
        }
        window.addEventListener('message', onMessage);

        var closedTimer = setInterval(function () {
            if (popup.closed) finish(false);
        }, 500);
    });
}

// ---------------------------------------------------------------------------
// renderUserInfo — fills the given element with either the signed-in user
// info (with Sign out link) or a Sign in link that opens the popup.
//   elementId : id of the container element (span/div)
//   options.linkColor : color for the Sign in / Sign out hyperlinks (CSS color)
// On successful popup sign-in, the page is reloaded so all data fetches re-run.
// ---------------------------------------------------------------------------
async function renderUserInfo(elementId, options) {
    options = options || {};
    var linkColor = options.linkColor || '#0078d4';
    var el = document.getElementById(elementId);
    if (!el) return;

    try {
        var res = await authFetch('/api/me');
        if (res.ok) {
            var u = await res.json();
            var emailPart = u.email ? ' (' + escapeAttr(u.email) + ')' : '';
            el.innerHTML =
                '👤 <strong>' + escapeAttr(u.name) + '</strong>' + emailPart +
                ' &mdash; <a href="/MicrosoftIdentity/Account/SignOut" style="color:' + linkColor + ';">Sign out</a>';
            return;
        }
    } catch (e) {
        // Network error — fall through to Sign in link.
    }

    // Not authenticated (or call failed) → show Sign in link.
    el.innerHTML =
        '🔐 Not signed in &mdash; ' +
        '<a href="#" id="' + elementId + '-signin" style="color:' + linkColor + ';font-weight:600;">Sign in</a>';

    var link = document.getElementById(elementId + '-signin');
    if (link) {
        link.addEventListener('click', async function (e) {
            e.preventDefault();
            link.textContent = 'Signing in…';
            var ok = await signInWithPopup();
            if (ok) {
                // Cookie is now set — reload so all data fetches use it.
                window.location.reload();
            } else {
                link.textContent = 'Sign in';
            }
        });
    }
}

function escapeAttr(text) {
    if (text === null || text === undefined) return '';
    var div = document.createElement('div');
    div.textContent = String(text);
    return div.innerHTML;
}

