using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Licensing.Activation;
using BoltMate.Licensing.Configuration;
using BoltMate.Licensing.Contracts;
using BoltMate.Licensing.Crypto;
using BoltMate.Licensing.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Licensing;

public sealed class LicenseGate : ILicenseGate, IDisposable
{
    private readonly ISecureStore _store;
    private readonly JwtVerifier _verifier;
    private readonly IBrowserAuthFlow _authFlow;
    private readonly IEntitlementClient _entitlements;
    private readonly IClock _clock;
    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseGate> _log;

    private readonly BehaviorSubject<LicenseStatus> _statusSubject = new(LicenseStatus.NotActivated);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseGate(
        ISecureStore store,
        JwtVerifier verifier,
        IBrowserAuthFlow authFlow,
        IEntitlementClient entitlements,
        IClock clock,
        LicensingOptions options,
        ILogger<LicenseGate>? log = null)
    {
        _store = store;
        _verifier = verifier;
        _authFlow = authFlow;
        _entitlements = entitlements;
        _clock = clock;
        _options = options;
        _log = log ?? NullLogger<LicenseGate>.Instance;
    }

    public LicenseStatus Current => _statusSubject.Value;

    public IObservable<LicenseStatus> StatusChanges => _statusSubject.AsObservable();

    public async Task<LicenseStatus> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jwt = await _store.GetAsync(_options.SecureStoreKey, ct).ConfigureAwait(false);
            var next = jwt is null
                ? LicenseStatus.NotActivated
                : EvaluateStored(jwt);
            return Publish(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseStatus> ActivateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log.LogInformation("Activation flow started.");
            var auth = await _authFlow.AuthenticateAsync(ct).ConfigureAwait(false);
            var response = await _entitlements.RequestEntitlementAsync(auth.IdToken, ct).ConfigureAwait(false);
            await _store.SetAsync(_options.SecureStoreKey, response.Jwt, ct).ConfigureAwait(false);
            var status = EvaluateStored(response.Jwt);
            _log.LogInformation("Activation succeeded: {State} tier={Tier} exp={Expiry}", status.State, status.Tier, status.ExpiresAt);
            return Publish(status);
        }
        catch (EntitlementRequestException ex) when (ex.IsRevoked)
        {
            await _store.DeleteAsync(_options.SecureStoreKey, ct).ConfigureAwait(false);
            _log.LogWarning("License revoked during activation.");
            return Publish(new LicenseStatus(LicenseState.Revoked, null, null, null, null, null, _clock.UtcNow));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseStatus> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _store.GetAsync(_options.SecureStoreKey, ct).ConfigureAwait(false);
            if (existing is null)
                return Publish(LicenseStatus.NotActivated);

            var current = EvaluateStored(existing);
            var now = _clock.UtcNow;

            var needsRefresh =
                current.ExpiresAt is null ||
                current.ExpiresAt - now <= _options.RefreshBeforeExpiry ||
                current.State is LicenseState.Expired or LicenseState.GracePeriod;

            if (!needsRefresh)
                return Publish(current);

            try
            {
                var auth = await _authFlow.AuthenticateAsync(ct).ConfigureAwait(false);
                var response = await _entitlements.RequestEntitlementAsync(auth.IdToken, ct).ConfigureAwait(false);
                await _store.SetAsync(_options.SecureStoreKey, response.Jwt, ct).ConfigureAwait(false);
                return Publish(EvaluateStored(response.Jwt));
            }
            catch (EntitlementRequestException ex) when (ex.IsRevoked)
            {
                await _store.DeleteAsync(_options.SecureStoreKey, ct).ConfigureAwait(false);
                _log.LogWarning("License revoked on refresh.");
                return Publish(new LicenseStatus(LicenseState.Revoked, null, current.Email, current.LicenseId, current.IssuedAt, current.ExpiresAt, now));
            }
            catch (EntitlementRequestException ex) when (ex.IsRateLimited)
            {
                _log.LogWarning("Refresh rate-limited. Retry-After={RetryAfter}s.", ex.RetryAfterSeconds);
                return Publish(current with { RefreshFailedSince = current.RefreshFailedSince ?? now });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Refresh failed; staying on cached status.");
                return Publish(current with { RefreshFailedSince = current.RefreshFailedSince ?? now });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(_options.SecureStoreKey, ct).ConfigureAwait(false);
            Publish(LicenseStatus.NotActivated);
        }
        finally
        {
            _gate.Release();
        }
    }

    private LicenseStatus EvaluateStored(string jwt)
    {
        var now = _clock.UtcNow;
        var result = _verifier.Verify(jwt, now);

        if (!result.IsValid && !result.IsExpired)
        {
            _log.LogWarning("License JWT failed verification: {Reason}", result.Reason);
            return new LicenseStatus(LicenseState.SignatureInvalid, null, null, null, null, null, null);
        }

        var claims = result.Claims!;
        var state = ClassifyState(claims, now);
        return new LicenseStatus(state, claims.Tier, claims.Email, claims.LicenseId, claims.IssuedAt, claims.ExpiresAt, null);
    }

    private LicenseState ClassifyState(LicenseClaims claims, DateTimeOffset now)
    {
        if (now <= claims.ExpiresAt)
            return LicenseState.Valid;
        if (now <= claims.ExpiresAt + _options.GracePeriod)
            return LicenseState.GracePeriod;
        return LicenseState.Expired;
    }

    private LicenseStatus Publish(LicenseStatus status)
    {
        if (!Equals(_statusSubject.Value, status))
            _statusSubject.OnNext(status);
        return status;
    }

    public void Dispose()
    {
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
        _gate.Dispose();
    }
}
