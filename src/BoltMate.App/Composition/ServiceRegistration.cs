using System;
using BoltMate.App.Permissions;
using BoltMate.App.Updates;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Switcher;
using BoltMate.Hid.Abstractions;
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
        services.AddSingleton<IReceiverTransport>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            if (OperatingSystem.IsMacOS())   return new BoltMate.Hid.IOKit.IOKitReceiverTransport(lf);
            if (OperatingSystem.IsWindows()) return new BoltMate.Hid.Win.WinReceiverTransport(lf);
            return new BoltMate.Hid.HidApi.HidApiReceiverTransport(lf);
        });

        services.AddSingleton<IPermissionsService, PermissionsService>();

        services.AddSingleton<ReceiverManager>(sp => new ReceiverManager(
            sp.GetRequiredService<IReceiverTransport>(),
            loggerFactory: sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<SwitcherService>(sp => new SwitcherService(
            sp.GetRequiredService<ReceiverManager>(),
            sp.GetRequiredService<ILogger<SwitcherService>>()));

        services.AddSingleton<DeviceEnricher>(sp => new DeviceEnricher(
            sp.GetRequiredService<ReceiverManager>(),
            sp.GetRequiredService<ILogger<DeviceEnricher>>()));

        services.AddSingleton<UpdateService>(sp => new UpdateService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<UpdateService>>()));

        return services;
    }
}
