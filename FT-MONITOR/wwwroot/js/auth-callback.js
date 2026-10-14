(function () {
    'use strict';

    function showError() {
        const status = document.getElementById('authStatus');
        status.setAttribute('role', 'alert');
        status.textContent = 'Microsoft sign-in could not return to WorkMonitor. Return to the app and select Sign in again. If this continues, ask your administrator to check the SPA redirect URI and browser storage permissions.';
    }

    if (!window.msalRedirectBridge) {
        showError();
        return;
    }
    // The supported v5 bridge handles popup/iframe BroadcastChannel responses
    // and redirect handoff to handleRedirectPromise on the originating page.
    window.msalRedirectBridge.broadcastResponseToMainFrame().catch(showError);
})();
