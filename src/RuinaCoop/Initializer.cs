using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace RuinaCoop
{
    public sealed class Initializer : ModInitializer
    {
        private const string ExpectedGameHash = "E450EF9DD29ABF5828491A891D86515D4AF35C21C81D8E076335B86F6231C901";
        private static bool _initialized;
        private static string _dependencyDirectory;

        public override void OnInitializeMod()
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                var actualHash = ComputeGameHash();
                if (!string.Equals(actualHash, ExpectedGameHash, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError("[RuinaCoop] Unsupported Assembly-CSharp.dll SHA-256: " + actualHash + ". Patches were not applied.");
                    return;
                }

                RegisterDependencyResolver();
                PatchInstaller.Install();
                _initialized = true;
                Debug.Log("[RuinaCoop] Stage 0 bootstrap loaded; game hash verified; Harmony patch registered.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[RuinaCoop] Bootstrap failed: " + exception);
            }
        }

        private static void RegisterDependencyResolver()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(Initializer).Assembly.Location);
            _dependencyDirectory = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "Dependencies"));
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
        }

        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            switch (name)
            {
                case "0Harmony":
                case "Mono.Cecil":
                case "Mono.Cecil.Mdb":
                case "Mono.Cecil.Pdb":
                case "Mono.Cecil.Rocks":
                case "MonoMod.RuntimeDetour":
                case "MonoMod.Utils":
                    var path = Path.Combine(_dependencyDirectory, name + ".dll");
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                default:
                    return null;
            }
        }

        private static string ComputeGameHash()
        {
            using (var stream = File.OpenRead(typeof(ModInitializer).Assembly.Location))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
            }
        }
    }

    internal static class GameUpdateProbe
    {
        private static bool _reported;

        private static void Postfix()
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
            Debug.Log("[RuinaCoop] Harmony game-loop probe reached.");
        }
    }
}