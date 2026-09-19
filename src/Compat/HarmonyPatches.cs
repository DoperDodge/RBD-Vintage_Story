using System;
using System.Reflection;
using HarmonyLib;
using Shinimodori.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Shinimodori.Compat
{
    /// <summary>
    /// The one static seam the Harmony patches talk to. Patched methods are static and
    /// run on arbitrary threads, so they must not reach into the mod system directly.
    /// Every field here is null until the server side wires it up, and every patch
    /// no-ops while it is.
    /// </summary>
    public static class ShinimodoriBridge
    {
        /// <summary>Set by the server system; null on a client or before the world loads.</summary>
        public static JournalRecorder Recorder;

        /// <summary>
        /// Called before any <see cref="Entity.Die"/>. Return true to cancel the death —
        /// which is how a blessed player never reaches the death state (§5.1).
        /// </summary>
        public static System.Func<Entity, EnumDespawnReason, DamageSource, bool> InterceptDie;

        /// <summary>True when the block-write patches applied; false means degraded journaling.</summary>
        public static bool BlockPatchesActive;
        /// <summary>True when the death patch applied; false means the post-death fallback is in use.</summary>
        public static bool DiePatchActive;

        /// <summary>
        /// Called after a player finishes writing on a sign. Return true when the words
        /// must not be allowed to stay (§7.2).
        /// </summary>
        public static System.Func<Vintagestory.API.Common.IPlayer, object, string, bool> OnTextWritten;

        public static void Clear()
        {
            Recorder = null;
            InterceptDie = null;
            OnTextWritten = null;
        }
    }

    /// <summary>
    /// Applies the mod's Harmony patches, individually wrapped. A patch that fails to
    /// apply is logged loudly and leaves a documented fallback in place (§19) — it never
    /// stops the mod from loading.
    /// </summary>
    public static class HarmonyPatches
    {
        public const string HarmonyId = "com.doperdodge.shinimodori";
        private static Harmony harmony;

        public static void Apply(ILogger logger)
        {
            if (harmony != null) return;

            try { harmony = new Harmony(HarmonyId); }
            catch (Exception e)
            {
                logger.Error("[shinimodori] Harmony could not start at all; journaling will only see " +
                             "player-driven changes and deaths will use the post-death fallback. {0}", e);
                return;
            }

            ShinimodoriBridge.BlockPatchesActive = PatchBlockWrites(logger);
            ShinimodoriBridge.DiePatchActive = PatchDie(logger);
            PatchSignWriting(logger);
        }

        public static void Unapply(ILogger logger)
        {
            try { harmony?.UnpatchAll(HarmonyId); }
            catch (Exception e) { logger?.Warning("[shinimodori] unpatch failed: {0}", e.Message); }
            harmony = null;
            ShinimodoriBridge.BlockPatchesActive = false;
            ShinimodoriBridge.DiePatchActive = false;
        }

        // ------------------------------------------------------------- block writes

        /// <summary>
        /// Block changes made by the world itself — fluids spreading, crops ripening,
        /// fire eating a roof — raise no player event. The only complete seam is the
        /// engine's own write path, so these three patches are what make the journal
        /// honest rather than merely player-shaped.
        /// </summary>
        private static bool PatchBlockWrites(ILogger logger)
        {
            bool ok = true;
            var accessorBase = AccessTools.TypeByName("Vintagestory.Common.BlockAccessorBase");
            var bulk = AccessTools.TypeByName("Vintagestory.Common.BlockAccessorRelaxedBulkUpdate");

            if (accessorBase == null || bulk == null)
            {
                logger.Error("[shinimodori] Could not find the engine's block accessor types. " +
                             "World-simulation changes (fluids, crops, fire) will NOT be journaled; " +
                             "returns will still restore players, blocks you touched, and entities.");
                return false;
            }

            ok &= TryPatch(logger, AccessTools.Method(accessorBase, "SetSolidBlockInternal"),
                           nameof(SolidBlockPrefix), "SetSolidBlockInternal");
            ok &= TryPatch(logger, AccessTools.Method(accessorBase, "SetFluidBlockInternal"),
                           nameof(FluidBlockPrefix), "SetFluidBlockInternal");
            ok &= TryPatch(logger, AccessTools.Method(bulk, "Commit"),
                           nameof(BulkCommitPrefix), "BlockAccessorRelaxedBulkUpdate.Commit");
            return ok;
        }

        private static bool TryPatch(ILogger logger, MethodBase target, string prefixName, string label)
        {
            if (target == null)
            {
                logger.Warning("[shinimodori] Patch target '{0}' not found; that change path will not be journaled.", label);
                return false;
            }
            try
            {
                var prefix = new HarmonyMethod(typeof(HarmonyPatches).GetMethod(prefixName,
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(target, prefix: prefix);
                return true;
            }
            catch (Exception e)
            {
                logger.Error("[shinimodori] Failed to patch '{0}': {1}", label, e.Message);
                return false;
            }
        }

        // Signature mirrors BlockAccessorBase.SetSolidBlockInternal; Harmony binds by name.
        private static void SolidBlockPrefix(BlockPos pos)
        {
            var rec = ShinimodoriBridge.Recorder;
            if (rec == null) return;
            try { rec.OnBlockWillChange(pos); } catch { /* never break a world write */ }
        }

        private static void FluidBlockPrefix(BlockPos pos)
        {
            var rec = ShinimodoriBridge.Recorder;
            if (rec == null) return;
            try { rec.OnBlockWillChange(pos); } catch { }
        }

        private static void BulkCommitPrefix(object __instance)
        {
            var rec = ShinimodoriBridge.Recorder;
            if (rec == null) return;
            try
            {
                if (__instance is IBulkBlockAccessor acc) rec.OnBulkWillCommit(acc);
            }
            catch { }
        }

        // ---------------------------------------------------------------- Entity.Die

        /// <summary>
        /// Deaths that never pass through damage — /kill, the void, another mod calling
        /// Die() outright — still have to become a return rather than a death screen (§5.1).
        /// </summary>
        private static bool PatchDie(ILogger logger)
        {
            var target = AccessTools.Method(typeof(Entity), nameof(Entity.Die),
                new[] { typeof(EnumDespawnReason), typeof(DamageSource) });
            if (target == null)
            {
                logger.Error("[shinimodori] Entity.Die not found; falling back to suppressing the " +
                             "death screen after the fact. Deaths will still become returns.");
                return false;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(HarmonyPatches).GetMethod(nameof(DiePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                logger.Error("[shinimodori] Failed to patch Entity.Die: {0}. Falling back to the post-death path.", e.Message);
                return false;
            }
        }

        // ----------------------------------------------------------- written words

        /// <summary>
        /// Writing the forbidden thing down is allowed. It is what happens next that is
        /// the horror: the letters go, one by one, and the board is blank (§7.2).
        /// Vanilla has no writable book, so signs and sign posts are the whole surface.
        /// </summary>
        private static void PatchSignWriting(ILogger logger)
        {
            var sign = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntitySign");
            if (sign == null)
            {
                logger.Warning("[shinimodori] BlockEntitySign not found; written text will not be screened.");
                return;
            }
            var target = AccessTools.Method(sign, "OnReceivedClientPacket");
            if (target == null) return;
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(HarmonyPatches).GetMethod(nameof(SignWrittenPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                logger.Warning("[shinimodori] Could not patch sign writing: {0}", e.Message);
            }
        }

        private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo> SignTextFields =
            new System.Collections.Generic.Dictionary<Type, FieldInfo>();

        private static void SignWrittenPostfix(object __instance, Vintagestory.API.Common.IPlayer player)
        {
            var hook = ShinimodoriBridge.OnTextWritten;
            if (hook == null || __instance == null || player == null) return;
            try
            {
                var type = __instance.GetType();
                if (!SignTextFields.TryGetValue(type, out var field))
                {
                    field = AccessTools.Field(type, "text");
                    SignTextFields[type] = field;
                }
                if (field == null) return;
                string text = field.GetValue(__instance) as string;
                if (string.IsNullOrWhiteSpace(text)) return;
                hook(player, __instance, text);
            }
            catch { }
        }

        /// <summary>Returns false to skip the original — i.e. to prevent the death.</summary>
        private static bool DiePrefix(Entity __instance, EnumDespawnReason reason, DamageSource damageSourceForDeath)
        {
            var hook = ShinimodoriBridge.InterceptDie;
            if (hook == null) return true;
            if (!(__instance is EntityPlayer)) return true;
            try
            {
                return !hook(__instance, reason, damageSourceForDeath);
            }
            catch
            {
                return true;   // on any doubt, let vanilla proceed
            }
        }
    }
}
