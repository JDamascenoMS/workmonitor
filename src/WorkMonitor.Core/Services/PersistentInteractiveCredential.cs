using Azure.Core;
using Azure.Identity;

namespace WorkMonitor.Core.Services;

public sealed record PersistentInteractiveCredentialOptions(
    string TenantId,
    string? LoginHint,
    string CacheName,
    string AuthenticationRecordPath,
    bool AllowInteractiveAuthentication = true);

public sealed class PersistentInteractiveCredential : TokenCredential
{
    private readonly PersistentInteractiveCredentialOptions _options;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private InteractiveBrowserCredential? _credential;
    private AuthenticationRecord? _record;
    private string? _lastRecoveryKey;

    public PersistentInteractiveCredential(
        PersistentInteractiveCredentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AuthenticationRecordPath);
        _options = options with
        {
            AuthenticationRecordPath = Path.GetFullPath(
                options.AuthenticationRecordPath)
        };
    }

    public string? AuthenticatedUsername => _record?.Username;

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var credential = await GetOrCreateCredentialAsync(cancellationToken);
        var activeRequestContext = requestContext;

        for (var recoveryAttempt = 0; ; recoveryAttempt++)
        {
            try
            {
                return await credential.GetTokenAsync(
                    activeRequestContext,
                    cancellationToken);
            }
            catch (AuthenticationRequiredException ex) when (
                _options.AllowInteractiveAuthentication && recoveryAttempt < 2)
            {
                activeRequestContext = ex.TokenRequestContext;
                credential = await RecoverCredentialAsync(
                    credential,
                    recoveryAttempt == 0 ? null : activeRequestContext,
                    cancellationToken);
            }
        }
    }

    public async Task<AuthenticationRecord> AuthenticateAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.AllowInteractiveAuthentication)
            throw new CredentialUnavailableException(
                "Interactive authentication is disabled for this host. Run workmonitor --auth-login first.");
        if (force)
        {
            await ResetAsync(cancellationToken);
        }

        await GetOrCreateCredentialAsync(cancellationToken);
        return _record
            ?? throw new InvalidOperationException(
                "Interactive authentication completed without an account record.");
    }

    public async Task<string?> GetCachedUsernameAsync(
        CancellationToken cancellationToken = default)
    {
        if (_record != null)
        {
            return _record.Username;
        }

        if (!File.Exists(_options.AuthenticationRecordPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            _options.AuthenticationRecordPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var record = await AuthenticationRecord.DeserializeAsync(
            stream,
            cancellationToken);
        ValidateRecord(record);
        return record.Username;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            _credential = null;
            _record = null;
            _lastRecoveryKey = null;
            if (File.Exists(_options.AuthenticationRecordPath))
            {
                File.Delete(_options.AuthenticationRecordPath);
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<InteractiveBrowserCredential> GetOrCreateCredentialAsync(
        CancellationToken cancellationToken)
    {
        if (_credential != null)
        {
            return _credential;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_credential != null)
            {
                return _credential;
            }

            if (File.Exists(_options.AuthenticationRecordPath))
            {
                await using var stream = new FileStream(
                    _options.AuthenticationRecordPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                _record = await AuthenticationRecord.DeserializeAsync(
                    stream,
                    cancellationToken);
                ValidateRecord(_record);
                _credential = CreateCredential(_record);
                return _credential;
            }

            if (!_options.AllowInteractiveAuthentication)
                throw new CredentialUnavailableException(
                    "No saved WorkMonitor account is available. Run workmonitor --auth-login first.");
            return await AuthenticateWithoutLockAsync(
                requestContext: null,
                cancellationToken);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<InteractiveBrowserCredential> RecoverCredentialAsync(
        InteractiveBrowserCredential failedCredential,
        TokenRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        var recoveryKey = BuildRecoveryKey(requestContext);
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_credential != null &&
                !ReferenceEquals(_credential, failedCredential) &&
                string.Equals(
                    _lastRecoveryKey,
                    recoveryKey,
                    StringComparison.Ordinal))
            {
                return _credential;
            }

            return await AuthenticateWithoutLockAsync(
                requestContext,
                cancellationToken);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<InteractiveBrowserCredential> AuthenticateWithoutLockAsync(
        TokenRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        var credential = CreateCredential(authenticationRecord: null);
        var record = requestContext.HasValue
            ? await credential.AuthenticateAsync(
                requestContext.Value,
                cancellationToken)
            : await credential.AuthenticateAsync(cancellationToken);
        ValidateRecord(record);
        await SaveRecordAsync(record, cancellationToken);
        _record = record;
        _credential = CreateCredential(record);
        _lastRecoveryKey = BuildRecoveryKey(requestContext);
        return _credential;
    }

    internal static string BuildRecoveryKey(TokenRequestContext? requestContext)
    {
        if (!requestContext.HasValue)
        {
            return "<account-bootstrap>";
        }

        var context = requestContext.Value;
        var scopes = string.Join(
            "\u001f",
            context.Scopes.OrderBy(
                scope => scope,
                StringComparer.OrdinalIgnoreCase));
        return string.Join(
            "\u001e",
            scopes,
            context.TenantId ?? string.Empty,
            context.Claims ?? string.Empty,
            context.IsCaeEnabled.ToString());
    }

    private InteractiveBrowserCredential CreateCredential(
        AuthenticationRecord? authenticationRecord)
    {
        return new InteractiveBrowserCredential(
            new InteractiveBrowserCredentialOptions
            {
                TenantId = _options.TenantId,
                LoginHint = _options.LoginHint,
                AuthenticationRecord = authenticationRecord,
                DisableAutomaticAuthentication = true,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = _options.CacheName
                }
            });
    }

    private void ValidateRecord(AuthenticationRecord record)
    {
        if (!string.Equals(
                record.TenantId,
                _options.TenantId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Authenticated tenant '{record.TenantId}' does not match configured " +
                $"tenant '{_options.TenantId}'. Run with --auth-reset and sign in again.");
        }

        if (!string.IsNullOrWhiteSpace(_options.LoginHint) &&
            !string.Equals(
                record.Username,
                _options.LoginHint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Authenticated account '{record.Username}' does not match configured " +
                $"account '{_options.LoginHint}'. Run with --auth-reset and sign in again.");
        }
    }

    private async Task SaveRecordAsync(
        AuthenticationRecord record,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.AuthenticationRecordPath)
            ?? throw new InvalidOperationException(
                "Authentication record directory could not be determined.");
        Directory.CreateDirectory(directory);
        var temporaryPath =
            _options.AuthenticationRecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await record.SerializeAsync(stream, cancellationToken);
            }

            File.Move(
                temporaryPath,
                _options.AuthenticationRecordPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
