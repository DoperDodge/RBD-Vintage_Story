using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Shinimodori.Core
{
    /// <summary>
    /// Captures and restores exactly the slice of a player that a return undoes.
    ///
    /// The asymmetry in §4.3 is enforced here and nowhere else: anything whose key
    /// starts with <see cref="ModPrefix"/> is the Witch's ledger, not a world object,
    /// and survives untouched. Knowledge (waypoints, handbook) lives outside the
    /// player entity entirely and is therefore never in reach of this code.
    /// </summary>
    public static class PlayerStateIO
    {
        /// <summary>Every attribute the mod owns is namespaced, and none are ever rewound.</summary>
        public const string ModPrefix = "sm:";

        /// <summary>
        /// Vanilla attributes that describe knowledge or bookkeeping rather than
        /// physical state, and so are deliberately left alone.
        /// </summary>
        private static readonly HashSet<string> PersistentVanillaKeys = new HashSet<string>
        {
            "lastRevivePosition",   // the vanilla respawn point; the mod owns respawning
            "characterName",
            "skinConfig",
            "voicetype",
            "voicepitch",
            "createdByPlayerName",
        };

        /// <summary>
        /// Inventories that must not be rewound: the creative-mode grid (not real
        /// storage) and the mouse cursor (transient, and clearing it mid-drag dupes).
        /// </summary>
        private static readonly HashSet<string> SkipInventoryClasses = new HashSet<string>
        {
            GlobalConstants.creativeInvClassName,
            GlobalConstants.mousecursorInvClassName,
        };

        // ------------------------------------------------------------------- capture

        public static PlayerSnapshot Capture(IServerPlayer plr)
        {
            var e = plr.Entity;
            var snap = new PlayerSnapshot
            {
                PlayerUID = plr.PlayerUID,
                X = e.ServerPos.X,
                Y = e.ServerPos.Y,
                Z = e.ServerPos.Z,
                Yaw = e.ServerPos.Yaw,
                Pitch = e.ServerPos.Pitch,
                Dimension = e.ServerPos.Dimension,
            };

            var watched = e.WatchedAttributes.Clone();
            StripModKeys(watched);
            snap.WatchedAttributes = ((TreeAttribute)watched).ToBytes();

            var attrs = e.Attributes.Clone();
            StripModKeys(attrs);
            snap.EntityAttributes = ((TreeAttribute)attrs).ToBytes();

            snap.Inventories = CaptureInventories(plr);
            return snap;
        }

        private static byte[] CaptureInventories(IServerPlayer plr)
        {
            var root = new TreeAttribute();
            foreach (var kv in plr.InventoryManager.Inventories)
            {
                var inv = kv.Value as InventoryBase;
                if (inv == null || SkipInventoryClasses.Contains(inv.ClassName)) continue;
                var sub = new TreeAttribute();
                inv.ToTreeAttributes(sub);
                root[kv.Key] = sub;
            }
            return root.ToBytes();
        }

        // ------------------------------------------------------------------- restore

        public static void Restore(ICoreServerAPI sapi, IServerPlayer plr, PlayerSnapshot snap)
        {
            var e = plr.Entity;

            RestoreInventories(plr, snap);
            RestoreTree(e.WatchedAttributes, snap.WatchedAttributes);
            RestoreTree(e.Attributes, snap.EntityAttributes);

            // Health and hunger live in WatchedAttributes sub-trees that were just
            // rewritten; nudge the behaviours so they pick the values back up.
            e.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>()?.UpdateMaxHealth();

            e.ServerPos.SetPos(snap.X, snap.Y, snap.Z);
            e.ServerPos.Yaw = snap.Yaw;
            e.ServerPos.Pitch = snap.Pitch;
            e.ServerPos.Dimension = snap.Dimension;
            e.ServerPos.Motion.Set(0, 0, 0);
            e.Pos.SetFrom(e.ServerPos);
            e.Pos.Motion.Set(0, 0, 0);

            e.TeleportTo(e.ServerPos);

            plr.BroadcastPlayerData(true);
            e.WatchedAttributes.MarkAllDirty();
        }

        private static void RestoreInventories(IServerPlayer plr, PlayerSnapshot snap)
        {
            if (snap.Inventories == null || snap.Inventories.Length == 0) return;

            var root = new TreeAttribute();
            root.FromBytes(snap.Inventories);

            foreach (var kv in plr.InventoryManager.Inventories)
            {
                var inv = kv.Value as InventoryBase;
                if (inv == null || SkipInventoryClasses.Contains(inv.ClassName)) continue;

                // Clear first, always: merging is how items duplicate across a rewind (§19).
                inv.Clear();

                var sub = root.GetTreeAttribute(kv.Key);
                if (sub != null) inv.FromTreeAttributes(sub);
                inv.MarkSlotDirty(-1);
            }
        }

        /// <summary>
        /// Replaces the live tree's contents with the snapshot's, while leaving the
        /// mod's own namespaced keys and the persistent vanilla keys exactly as they are.
        /// </summary>
        private static void RestoreTree(ITreeAttribute live, byte[] blob)
        {
            if (blob == null || blob.Length == 0) return;

            var snap = new TreeAttribute();
            snap.FromBytes(blob);

            // Drop live keys that the snapshot does not have (things gained since the anchor).
            var liveKeys = live.Select(p => p.Key).ToArray();
            foreach (var key in liveKeys)
            {
                if (IsPersistent(key)) continue;
                if (!snap.HasAttribute(key)) live.RemoveAttribute(key);
            }

            // Write the snapshot's values back over the rest.
            foreach (var pair in snap)
            {
                if (IsPersistent(pair.Key)) continue;
                live[pair.Key] = pair.Value.Clone();
            }
        }

        private static bool IsPersistent(string key) =>
            key.StartsWith(ModPrefix, StringComparison.Ordinal) || PersistentVanillaKeys.Contains(key);

        private static void StripModKeys(ITreeAttribute tree)
        {
            foreach (var key in tree.Select(p => p.Key).ToArray())
                if (IsPersistent(key)) tree.RemoveAttribute(key);
        }
    }
}
