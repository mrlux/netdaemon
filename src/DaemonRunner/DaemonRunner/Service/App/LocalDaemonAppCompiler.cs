using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using NetDaemon.Common;
using NetDaemon.Infrastructure.Extensions;

namespace NetDaemon.Service.App
{
    public class LocalDaemonAppCompiler : IDaemonAppCompiler
    {
        private readonly ILogger<DaemonAppCompiler> _logger;

        public LocalDaemonAppCompiler(ILogger<DaemonAppCompiler> logger)
        {
            _logger = logger;
        }

        public IEnumerable<Type> GetApps()
        {
            _logger.LogDebug("Loading local assembly apps...");
            
            var assemblies = LoadAll();
            var apps = assemblies.SelectMany(x => x.GetTypesWhereSubclassOf<NetDaemonAppBase>()).ToList();

            if (!apps.Any())
                _logger.LogWarning("No local daemon apps found.");
            else
                _logger.LogDebug("Found total of {nr_of_apps} apps", apps.Count());

            return apps;
        }

        private IEnumerable<Assembly> LoadAll()
        {
            var binFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)!;
            var netDaemonDlls = Directory.GetFiles(binFolder, "NetDaemon.*.dll");

            var alreadyLoadedAssemblies = AssemblyLoadContext.Default.Assemblies
                .Where(x => !x.IsDynamic)
                .Select(x => x.Location)
                .ToList();

            var netDaemonDllsToLoadDynamically = netDaemonDlls.Except(alreadyLoadedAssemblies);

            foreach (var netDaemonDllToLoadDynamically in netDaemonDllsToLoadDynamically)
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(netDaemonDllToLoadDynamically);
            }

            return AssemblyLoadContext.Default.Assemblies;
        }
    }
}