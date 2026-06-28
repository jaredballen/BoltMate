using BoltMate.Core.Services;
using System;
using BoltMate.App.Services;
using BoltMate.Core;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;
using BoltMate.Hid.Abstractions;
using BoltMate.Licensing.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BoltMate.App.Composition;

/// <summary>
/// Composition root for the tray app. Registers long-lived singleton
/// services with the container; resolution order is encoded by the
/// dependency graph (the container materialises in topological order).
/// </summary>
/// <remarks>
/// Scope is intentionally narrow:
/// <list type="bullet">
/// <item>Long-lived data + behaviour services live here.</item>
/// <item>UI controllers (<c>TrayMenuController</c>, <c>TrayIconStatusController</c>)
///       and windows stay constructed by <c>App.axaml.cs</c> — they require
///       the live <c>TrayIcon</c> / <c>Application</c> instance which DI
///       can't supply at registration time.</item>
/// <item>Topology stack (<c>UdpTopologyService</c>, <c>MdnsTcpChannel</c>,
///       <c>TopologyCorrelator</c>) is dynamically created / disposed when
///       the user toggles cross-machine sync — stays under
///       <c>App.ApplyTopologySettings</c> orchestration.</item>
/// </list>
/// Container disposal at app exit handles <c>IDisposable</c> teardown
/// for every registered service, replacing the previous manual
/// <c>CompositeDisposable</c> for these entries.
/// </remarks>
public static class ServiceRegistration
{
    public static IServiceCollection AddBoltMateCore(
        this IServiceCollection services,
        ILoggerFactory loggerFactory,
        AppSettings settings)
    {
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(settings);

        // TimeProvider.System for production; tests substitute FakeTimeProvider
        // directly when constructing services. Registered so any future
        // DI-injected service that takes TimeProvider gets the system clock.
        services.AddSingleton(TimeProvider.System);

        // IReceiverTransport is registered as a FACTORY (not a pre-built
        // instance) so the OS-specific HID handle doesn't open until the
        // first resolve. On macOS the IOKit transport's constructor calls
        // IOHIDManagerOpen — which triggers the Input Monitoring TCC prompt.
        // We want that prompt to fire ONLY after the welcome wizard has
        // primed the user via the HID primer page, so the first resolve
        // must happen inside App.ContinueBootstrap (which only runs after
        // wizard completion). Don't resolve this from Program.Main.
        services.AddSingleton<IPermissionsService>(sp => new PermissionsService(
            sp.GetService<BoltMate.App.Core.Notifications.INotificationService>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<INetworkAvailabilityWatcher, NetworkAvailabilityWatcher>();

        services.AddSingleton<IReceiverTransport>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            // Input Monitoring gate — passed to the Mac transport so
            // IOHIDManagerOpen / hid_open are deferred until the OS has
            // granted access. Without this, those calls trigger the TCC
            // system prompt at the wrong moment (process start vs. wizard).
            var perms = sp.GetRequiredService<IPermissionsService>();
            Func<bool> hidGate = () => perms.InputMonitoring.IsGranted;

            var hidGrantChanges = perms.InputMonitoring.IsGrantedChanged;

            if (OperatingSystem.IsMacOS())   return new BoltMate.Hid.IOKit.IOKitReceiverTransport(lf, hidGate, hidGrantChanges);
            if (OperatingSystem.IsWindows()) return new BoltMate.Hid.Win.WinReceiverTransport(lf);
            return new BoltMate.Hid.HidApi.HidApiReceiverTransport(lf, hidGate);
        });

        services.AddSingleton<IReceiverManager>(sp => new ReceiverManager(
            sp.GetRequiredService<IReceiverTransport>(),
            loggerFactory: sp.GetRequiredService<ILoggerFactory>()));

        // macOS USB hot-plug notifier — fires near-instant when a Bolt
        // receiver attaches or detaches via IOServiceAddMatchingNotification
        // (a different IOKit object class than IOHIDDevice, which can't be
        // safely callback-subscribed from .NET; see
        // reference_iohidmanager_threading memory). The notifier itself
        // does NO IOKit property reads — it only signals a managed
        // observable. App.ContinueBootstrap subscribes to drive an
        // immediate ReceiverManager.Refresh() on each event so hot-plug
        // surfaces in the UI sub-second instead of waiting for the 2s
        // poll tick.
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<BoltMate.Hid.IOKit.UsbBoltNotifier>(sp =>
                new BoltMate.Hid.IOKit.UsbBoltNotifier(
                    sp.GetRequiredService<ILogger<BoltMate.Hid.IOKit.UsbBoltNotifier>>()));
        }

        services.AddSingleton<SwitcherService>(sp => new SwitcherService(
            sp.GetRequiredService<IReceiverManager>(),
            sp.GetRequiredService<ILogger<SwitcherService>>()));

        services.AddSingleton<DeviceEnricher>(sp => new DeviceEnricher(
            sp.GetRequiredService<IReceiverManager>(),
            sp.GetRequiredService<ILogger<DeviceEnricher>>()));

        services.AddSingleton<UpdateService>(sp => new UpdateService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<UpdateService>>()));

        // Cross-platform notification dispatcher. INotificationService is
        // defined in BoltMate.App.Core; impls live in BoltMate.App.Win
        // (Microsoft.WindowsAppSDK / AppNotificationManager) and
        // BoltMate.App.Mac (Microsoft.macOS / UNUserNotificationCenter).
        // Selected here by runtime OS; the unused platform's project ref
        // doesn't fold into the wrong-TFM publish because the references
        // are TFM-conditioned in BoltMate.App.csproj.
#if WINDOWS
        services.AddSingleton<BoltMate.App.Core.Notifications.INotificationService>(sp =>
            new BoltMate.App.Win.Notifications.WinAppSdkNotificationService(
                sp.GetRequiredService<ILogger<BoltMate.App.Win.Notifications.WinAppSdkNotificationService>>()));
#elif MACOS
        services.AddSingleton<BoltMate.App.Core.Notifications.INotificationService>(sp =>
            new BoltMate.App.Mac.Notifications.MacUserNotificationService(
                sp.GetRequiredService<ILogger<BoltMate.App.Mac.Notifications.MacUserNotificationService>>()));
#endif

        // Topology stack — singletons. Each is gated on its permission
        // and the NIC watcher; the actual bind only happens after Start()
        // is called. The Settings → Network toggle calls Start/Stop on
        // these instead of destroying and re-creating, so the DI graph
        // stays simple.
        // Hardware-derived MachineId — same value across reboots, no
        // persisted GUID in AppSettings. Salted SHA-256 of the OS
        // IOPlatformUUID / MachineGuid / /etc/machine-id (whichever
        // platform we're on).
        services.AddSingleton<IMachineIdProvider, HardwareMachineIdProvider>();

        // Peer crypto — AES-GCM-wraps every cross-machine UDP + TCP
        // frame. Key is resolved through IPeerCryptoKeySource (the
        // LicenseGate). Signed out → no key → topology layer skips
        // sends and drops receives.
        services.AddSingleton<IPeerCryptoProvider>(sp => new AesGcmPeerCryptoProvider(
            () => sp.GetRequiredService<BoltMate.Licensing.IPeerCryptoKeySource>().GetCurrentKey()));

        services.AddSingleton<IUdpTopologyService>(sp => new UdpTopologyService(
            sp.GetRequiredService<IReceiverManager>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IMachineIdProvider>(),
            peerCrypto: sp.GetRequiredService<IPeerCryptoProvider>(),
            networkPermission: sp.GetRequiredService<IPermissionsService>().Network,
            networkAvailability: sp.GetRequiredService<INetworkAvailabilityWatcher>(),
            logger: sp.GetRequiredService<ILogger<UdpTopologyService>>()));

        services.AddSingleton<IMdnsTcpChannel>(sp => new MdnsTcpChannel(
            sp.GetRequiredService<IUdpTopologyService>(),
            sp.GetRequiredService<AppSettings>(),
            peerCrypto: sp.GetRequiredService<IPeerCryptoProvider>(),
            networkPermission: sp.GetRequiredService<IPermissionsService>().Network,
            networkAvailability: sp.GetRequiredService<INetworkAvailabilityWatcher>(),
            logger: sp.GetRequiredService<ILogger<MdnsTcpChannel>>()));

        services.AddSingleton<ITopologyCorrelator>(sp => new TopologyCorrelator(
            sp.GetRequiredService<IReceiverManager>(),
            sp.GetRequiredService<SwitcherService>(),
            sp.GetRequiredService<IUdpTopologyService>(),
            sp.GetRequiredService<ILogger<TopologyCorrelator>>()));

        // App health monitor. Pure observable surface — the App layer
        // subscribes to Health to wire tray badges + OS notifications.
        services.AddSingleton<IAppHealthService>(sp => new AppHealthService(
            sp.GetRequiredService<IPermissionsService>(),
            sp.GetRequiredService<IReceiverManager>(),
            sp.GetRequiredService<IUdpTopologyService>(),
            sp.GetRequiredService<IMdnsTcpChannel>(),
            sp.GetRequiredService<ILogger<AppHealthService>>()));

        // Licensing — LicenseGate + LoopbackAuthFlow + JwtVerifier +
        // Keychain/DPAPI secure store, all driven off the scaffolded
        // BoltMate.Licensing extension. Endpoint + OAuth client settings
        // hard-coded to the production Entra External ID + Function App
        // wired up in Phase 0. The JWT public key is the RSA verify-only
        // partner of `boltmate-jwt-signing` in KeyVault; it's NOT a
        // secret (anyone who downloads the app can read it), so it
        // lives in code rather than rotating through KV at runtime.
        services.AddBoltMateLicensing(opts =>
        {
            opts.ServiceName = "BoltMate";
            opts.SecureStoreKey = "com.jaredballen.BoltMate.entitlement";

            opts.Issuer = "https://api.boltmate.app";
            opts.EntitlementEndpoint = new Uri("https://api.boltmate.app/api/entitlement");

            // Entra External ID OAuth2 endpoints for the BoltMate tenant.
            opts.AuthorizeEndpoint = new Uri(
                "https://boltmateauth.ciamlogin.com/da0651cd-08cf-42d3-bf57-e36b3c960aee/oauth2/v2.0/authorize");
            opts.TokenEndpoint = new Uri(
                "https://boltmateauth.ciamlogin.com/da0651cd-08cf-42d3-bf57-e36b3c960aee/oauth2/v2.0/token");
            opts.OAuthClientId = "fe3bbdfe-a387-4b8f-be0a-03c5b3e9e593";
            opts.OAuthScopes = new[] { "openid", "email", "profile", "offline_access" };

            // Filled in after Phase 6 publishes a stable signing key —
            // until then LicenseGate.EvaluateStored returns
            // SignatureInvalid and the wizard falls back to a fresh
            // OAuth round-trip on every launch.
            opts.PublicKeyPem = string.Empty;
        });

        return services;
    }
}
