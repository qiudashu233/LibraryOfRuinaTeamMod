using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace RuinaCoop
{
    internal static class PatchInstaller
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install()
        {
            var original = typeof(GlobalGameManager).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var postfix = typeof(GameUpdateProbe).GetMethod(
                "Postfix",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (original == null || postfix == null)
            {
                throw new MissingMethodException("Stage 0 game-loop probe target is missing.");
            }

            var harmony = new Harmony("ruinacoop.bootstrap");
            try
            {
                DeckGuard.Install(harmony);
                NativeDeckEditor.Install(harmony);
                NativeEquipmentEditor.Install(harmony);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
        }
    }
}
