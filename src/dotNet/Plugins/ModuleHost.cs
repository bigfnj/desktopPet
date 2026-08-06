using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// Loads plugin module DLLs from a folder, each in its own collectible <see cref="AssemblyLoadContext"/>,
    /// while sharing the single DesktopPet.Contracts assembly from the default context so IModule/IHost
    /// types unify across host and modules. A module that fails to load or init is isolated (logged +
    /// skipped) so one bad module can never take the host down. Sideload layout: &lt;modulesRoot&gt;/&lt;id&gt;/&lt;id&gt;.dll.
    /// </summary>
    internal sealed class ModuleHost : IDisposable
    {
        private sealed class Loaded { public IModule Module; public ModuleLoadContext Alc; public string Directory; }
        private readonly List<Loaded> _loaded = new List<Loaded>();

        public IReadOnlyList<IModule> Modules { get { return _loaded.Select(l => l.Module).ToList(); } }

        /// <summary>Scan the modules root and load every module folder. Returns the count loaded.</summary>
        public int LoadFrom(string modulesRoot, IHost host, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot)) return 0;
            int count = 0;
            foreach (string dir in Directory.GetDirectories(modulesRoot))
            {
                ModuleLoadContext alc = null;
                try
                {
                    string dll = FindModuleDll(dir);
                    if (dll == null) continue;
                    alc = new ModuleLoadContext(dll);
                    Assembly asm = alc.LoadFromAssemblyPath(dll);
                    Type moduleType = asm.GetTypes().FirstOrDefault(t => !t.IsAbstract && typeof(IModule).IsAssignableFrom(t));
                    if (moduleType == null) { alc.Unload(); continue; }
                    var module = (IModule)Activator.CreateInstance(moduleType);
                    module.Init(host);
                    _loaded.Add(new Loaded { Module = module, Alc = alc, Directory = dir });
                    count++;
                    if (log != null) log("module loaded: " + module.Info.Id + " " + module.Info.Version);
                }
                catch (Exception ex)
                {
                    if (log != null) log("module load failed in '" + dir + "': " + ex.GetType().Name + ": " + ex.Message);
                    if (alc != null) { try { alc.Unload(); } catch { } }
                }
            }
            return count;
        }

        private static string FindModuleDll(string dir)
        {
            // Prefer <foldername>.dll; otherwise the first non-contract dll in the folder.
            string preferred = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(preferred)) return preferred;
            foreach (string f in Directory.GetFiles(dir, "*.dll"))
                if (!Path.GetFileName(f).Equals("DesktopPet.Contracts.dll", StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        public void ShutdownAll(Action<string> log)
        {
            foreach (Loaded l in _loaded)
            {
                try { l.Module.Shutdown(); }
                catch (Exception ex) { if (log != null) log("module shutdown error (" + l.Module.Info.Id + "): " + ex.Message); }
                try { l.Alc.Unload(); } catch { }
            }
            _loaded.Clear();
        }

        public void Dispose() { ShutdownAll(null); }

        /// <summary>
        /// Per-module load context. The <c>Load</c> override returns null for DesktopPet.Contracts so it
        /// resolves from the default context (a single shared contract assembly = unified IModule/IHost
        /// types); the module's own dependencies resolve from its folder via the dependency resolver.
        /// </summary>
        private sealed class ModuleLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            public ModuleLoadContext(string moduleDll)
                : base("module:" + Path.GetFileNameWithoutExtension(moduleDll), true)
            { _resolver = new AssemblyDependencyResolver(moduleDll); }

            protected override Assembly Load(AssemblyName name)
            {
                if (name.Name == "DesktopPet.Contracts") return null;   // share from the default context
                string path = _resolver.ResolveAssemblyToPath(name);
                return path != null ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
