using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetDaemon.Common.Configuration;
using NetDaemon.Daemon;
using NetDaemon.Daemon.Config;
using NetDaemon.Service.App;

namespace NetDaemon.Service
{
    public class RunnerService : BackgroundService
    {
        /// <summary>
        /// The interval used when disconnected
        /// </summary>
        private const int ReconnectInterval = 30000;
        private const string Version = "custom compiled";

        private readonly HomeAssistantSettings _homeAssistantSettings;
        private readonly NetDaemonSettings _netDaemonSettings;

        private readonly ILogger<RunnerService> _logger;

        private readonly IServiceProvider _serviceProvider;
        private readonly IYamlConfig _yamlConfig;
        private readonly IDaemonAppCompiler _daemonAppCompiler;

        private bool _entitiesGenerated;
        private IEnumerable<Type>? _loadedDaemonApps;

        private string? _sourcePath = null;

        public RunnerService(
            ILoggerFactory loggerFactory,
            IOptions<NetDaemonSettings> netDaemonSettings,
            IOptions<HomeAssistantSettings> homeAssistantSettings,
            IServiceProvider serviceProvider,
            IYamlConfig yamlConfig,
            IDaemonAppCompiler daemonAppCompiler
            )
        {
            _logger = loggerFactory.CreateLogger<RunnerService>();
            _homeAssistantSettings = homeAssistantSettings.Value;
            _netDaemonSettings = netDaemonSettings.Value;
            _serviceProvider = serviceProvider;
            _yamlConfig = yamlConfig;
            _daemonAppCompiler = daemonAppCompiler;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping NetDaemon...");
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_netDaemonSettings == null)
                {
                    _logger.LogError("No config specified, file or environment variables! Exiting...");
                    return;
                }

                _sourcePath = _netDaemonSettings.GetAppSourceDirectory();

                // EnsureApplicationDirectoryExists(_netDaemonSettings);


                _logger.LogDebug("Finding apps in {folder}...", _sourcePath);

                // Automatically create source directories
                if (_sourcePath is string && !Directory.Exists(_sourcePath))
                    throw new FileNotFoundException("Source path {path} does not exist", _sourcePath);

                var hasConnectedBefore = false;

                _loadedDaemonApps = null;

                await using var daemonHost = _serviceProvider.GetService<NetDaemonHost>()
                    ?? throw new ApplicationException("Failed to get service for NetDaemonHost");

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (hasConnectedBefore)
                        {
                            // This is due to re-connect, it must be a re-connect
                            // so delay before retry connect again
                            await Task.Delay(ReconnectInterval, stoppingToken).ConfigureAwait(false); // Wait x seconds
                            _logger.LogInformation($"Restarting NeDaemon (version {Version})...");
                        }

                        var daemonHostTask = daemonHost.Run(
                            _homeAssistantSettings.Host,
                            _homeAssistantSettings.Port,
                            _homeAssistantSettings.Ssl,
                            _homeAssistantSettings.Token,
                            stoppingToken
                        );

                        if (await WaitForDaemonToConnect(daemonHost, stoppingToken).ConfigureAwait(false) == false)
                        {
                            continue;
                        }

                        if (!stoppingToken.IsCancellationRequested)
                        {
                            if (daemonHost.Connected)
                            {
                                try
                                {
                                    // Generate code if requested
                                    if (_sourcePath is string)
                                        await GenerateEntities(daemonHost, _sourcePath);

                                    if (_loadedDaemonApps is null)
                                        _loadedDaemonApps = _daemonAppCompiler.GetApps();

                                    if (_loadedDaemonApps is null || !_loadedDaemonApps.Any())
                                    {
                                        _logger.LogError("No apps found, exiting...");
                                        return;
                                    }

                                    IInstanceDaemonApp? codeManager = new CodeManager(_loadedDaemonApps, _logger, _yamlConfig);
                                    await daemonHost.Initialize(codeManager).ConfigureAwait(false);

                                    // Wait until daemon stops
                                    await daemonHostTask.ConfigureAwait(false);
                                    if (!stoppingToken.IsCancellationRequested)
                                    {
                                        // It is disconnected, wait
                                        // _logger.LogWarning($"Home assistant is unavailable, retrying in {ReconnectInterval / 1000} seconds...");
                                    }
                                }
                                catch (TaskCanceledException)
                                {
                                    _logger.LogInformation("Canceling NetDaemon service...");
                                }
                                catch (Exception e)
                                {
                                    _logger.LogError(e, "Failed to load applications");
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"Home Assistant Core still unavailable, retrying in {ReconnectInterval / 1000} seconds...");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogWarning($"Home assistant is disconnected, retrying in {ReconnectInterval / 1000} seconds...");
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "MAJOR ERROR!");
                    }
                    finally
                    {
                        await daemonHost.Stop().ConfigureAwait(false);
                    }

                    // If we reached here it could be a re-connect
                    hasConnectedBefore = true;

                }
            }
            catch (OperationCanceledException)
            {
            } // Normal exit
            catch (Exception e)
            {
                _logger.LogError(e, "NetDaemon had unhandled exception, closing...");
            }

            _logger.LogInformation("NetDaemon exited!");
        }

        private async Task GenerateEntities(NetDaemonHost daemonHost, string sourceFolder)
        {
            if (!_netDaemonSettings.GenerateEntities.GetValueOrDefault())
                return;

            if (_entitiesGenerated)
                return;

            _logger.LogDebug("Generating entities ..");

            _entitiesGenerated = true;
            var codeGen = new CodeGenerator();
            var source = codeGen.GenerateCode(
                "Netdaemon.Generated.Extensions",
                daemonHost.State.Select(n => n.EntityId).Distinct()
            );

            await File.WriteAllTextAsync(Path.Combine(sourceFolder!, "_EntityExtensions.cs"), source).ConfigureAwait(false);

            var services = await daemonHost.GetAllServices();
            var sourceRx = codeGen.GenerateCodeRx(
                "Netdaemon.Generated.Reactive",
                daemonHost.State.Select(n => n.EntityId).Distinct(),
                services
            );

            await File.WriteAllTextAsync(Path.Combine(sourceFolder!, "_EntityExtensionsRx.cs"), sourceRx).ConfigureAwait(false);
        }

        // private void EnsureApplicationDirectoryExists(NetDaemonSettings settings)
        // {
        //     settings.SourceFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netdaemon");
        //     var appDirectory = Path.Combine(settings.SourceFolder, "apps");

        //     Directory.CreateDirectory(appDirectory);
        // }

        private async Task<bool> WaitForDaemonToConnect(NetDaemonHost daemonHost, CancellationToken stoppingToken)
        {
            var nrOfTimesCheckForConnectedState = 0;

            while (!daemonHost.Connected && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                if (nrOfTimesCheckForConnectedState++ > 5)
                    break;
            }
            return daemonHost.Connected;
        }
    }
}