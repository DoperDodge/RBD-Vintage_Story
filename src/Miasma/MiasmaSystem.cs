using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Death;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Miasma
{
    /// <summary>
    /// The Witch's scent (§6). Gained by returning, shed by making progress.
    ///
    /// This is the compounding cost in P4: more returns mean more scent, more scent
    /// means a more hostile world, and a more hostile world means more returns. The
    /// only way out is forward, which the decay curve says without ever saying it.
    /// </summary>
    public class MiasmaSystem
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private ShinimodoriConfig Cfg => server.Cfg;
        private MiasmaConfig M => server.Cfg.Miasma;

        private long tickListener = -1;
        /// <summary>MinValue until the first tick: the calendar does not exist yet at StartServerSide.</summary>
        private double lastDecayHours = double.MinValue;
        /// <summary>Living mabeasts per player, so a pack does not become an infestation.</summary>
        private readonly Dictionary<string, List<long>> packs = new Dictionary<string, List<long>>();
        private double lastPackRollHours = double.MinValue;
        /// <summary>Temporal gears each player was carrying last tick, to spot one being spent.</summary>
        private readonly Dictionary<string, int> gearCount = new Dictionary<string, int>();
        /// <summary>When each player last saw something at the edge of their vision.</summary>
        private readonly Dictionary<string, double> lastSilhouetteHours = new Dictionary<string, double>();

        public MiasmaSystem(ShinimodoriServer server) { this.server = server; }

        public void Register()
        {
            tickListener = Api.Event.RegisterGameTickListener(OnTick, 2000);
            Api.Event.OnEntitySpawn += OnEntitySpawn;
            Api.Event.OnEntityLoaded += OnEntitySpawn;

            var stability = Api.ModLoader.GetModSystem<SystemTemporalStability>();
            if (stability != null) stability.OnGetTemporalStability += OnGetTemporalStability;
        }

        public void Unregister()
        {
            if (tickListener != -1) { Api.Event.UnregisterGameTickListener(tickListener); tickListener = -1; }
            Api.Event.OnEntitySpawn -= OnEntitySpawn;
            Api.Event.OnEntityLoaded -= OnEntitySpawn;

            var stability = Api.ModLoader.GetModSystem<SystemTemporalStability>();
            if (stability != null) stability.OnGetTemporalStability -= OnGetTemporalStability;
        }

        // ------------------------------------------------------------------- tiers

        public int TierIndexFor(IServerPlayer plr, PlayerState ps)
        {
            int tier = 0;
            for (int i = 0; i < M.Tiers.Count; i++)
                if (ps.Miasma >= M.Tiers[i].Min) tier = i;

            // The pelt cloak is the only real counterplay the world offers (§6.4).
            if (plr != null && IsWearingPeltCloak(plr))
                tier = Math.Max(0, tier - M.PeltCloakTierReduction);

            return tier;
        }

        public MiasmaTierConfig Tier(int index) =>
            M.Tiers[Math.Max(0, Math.Min(index, M.Tiers.Count - 1))];

        public float DetectionMultiplierFor(int tier) => Tier(tier).DetectionMultiplier;
        public bool TradersRefuseAt(int tier) => Tier(tier).TradersRefuse;

        /// <summary>
        /// True while the player is carrying the mabeast cloak. Vanilla has no cloak
        /// equipment slot to hang it on, so carrying it is what counts — which also
        /// means it costs you a stack of inventory for as long as you want the cover.
        /// </summary>
        public bool IsWearingPeltCloak(IServerPlayer plr)
        {
            var invs = plr?.InventoryManager?.Inventories;
            if (invs == null) return false;

            foreach (var kv in invs)
            {
                if (kv.Value == null) continue;
                if (kv.Value.ClassName == Vintagestory.API.Config.GlobalConstants.creativeInvClassName) continue;
                foreach (var slot in kv.Value)
                {
                    var collectible = slot?.Itemstack?.Collectible;
                    if (collectible == null) continue;
                    if (collectible.Attributes?["shinimodoriPeltCloak"].AsBool() == true) return true;
                }
            }
            return false;
        }

        /// <summary>The nearest blessed player and their effective tier, for the aggro behaviours.</summary>
        public (IServerPlayer player, int tier) NearestBlessedScent(Vec3d from, double maxDistance)
        {
            IServerPlayer best = null;
            double bestDist = maxDistance * maxDistance;
            foreach (var p in server.BlessedPlayers())
            {
                if (p.Entity == null) continue;
                double d = p.Entity.Pos.XYZ.SquareDistanceTo(from);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best == null ? (null, 0) : (best, TierIndexFor(best, server.StateOf(best)));
        }

        // ------------------------------------------------------------------ gain

        public void OnReturn(IServerPlayer plr, PlayerState ps, DeathCause cause, bool voluntary)
        {
            if (!M.Enabled) return;

            float gain = M.GainPerReturn;
            if (voluntary) gain *= M.VoluntaryMultiplier;       // she notices eagerness
            if (cause == DeathCause.Taboo) gain *= M.TabooMultiplier;

            ps.Miasma = Math.Min(100f, ps.Miasma + gain);
            server.Debug($"{plr.PlayerName} miasma -> {ps.Miasma:F1} (+{gain:F1})");
        }

        /// <summary>
        /// Notices a temporal gear being spent, however it was spent — repairing a
        /// translocator, feeding a rift, or another mod's use of it. Counting what the
        /// player carries beats patching one specific item class, and it keeps working
        /// when somebody else adds a new way to burn one.
        /// </summary>
        private void WatchForSpentGear(IServerPlayer plr, PlayerState ps)
        {
            int now = CountTemporalGears(plr);
            if (gearCount.TryGetValue(plr.PlayerUID, out int before) && now < before)
                TryBurnWithTemporalGear(plr, ps);
            gearCount[plr.PlayerUID] = now;
        }

        private static int CountTemporalGears(IServerPlayer plr)
        {
            var invs = plr?.InventoryManager?.Inventories;
            if (invs == null) return 0;
            int n = 0;
            foreach (var kv in invs)
            {
                if (kv.Value == null) continue;
                if (kv.Value.ClassName == Vintagestory.API.Config.GlobalConstants.creativeInvClassName) continue;
                foreach (var slot in kv.Value)
                {
                    var code = slot?.Itemstack?.Collectible?.Code?.Path;
                    if (code != null && code.StartsWith("gear-temporal", StringComparison.Ordinal))
                        n += slot.Itemstack.StackSize;
                }
            }
            return n;
        }

        /// <summary>
        /// A figure at the far edge of the screen, for two seconds, in the direction
        /// the player is not looking. No sound. No mechanical effect. Ever (§12.5).
        /// </summary>
        private void RollPeripheralSilhouette(IServerPlayer plr, PlayerState ps, double now)
        {
            if (!Cfg.Visuals.PeripheralSilhouettes) return;
            if (ps.Miasma < M.ClingThreshold) return;

            double interval = Cfg.Visuals.SilhouetteIntervalMinutes / 60.0;
            if (lastSilhouetteHours.TryGetValue(plr.PlayerUID, out double last) && now - last < interval) return;
            if (!lastSilhouetteHours.ContainsKey(plr.PlayerUID))
            {
                // Do not fire the instant they cross the threshold.
                lastSilhouetteHours[plr.PlayerUID] = now;
                return;
            }

            lastSilhouetteHours[plr.PlayerUID] = now;
            server.SendCue(plr, "silhouette", 1f, 2f);
        }

        /// <summary>Consuming a temporal gear burns the scent off — the one lever, and it costs (§8).</summary>
        public bool TryBurnWithTemporalGear(IServerPlayer plr, PlayerState ps)
        {
            if (!M.Enabled) return false;
            if (TierIndexFor(plr, ps) < 3) return false;
            ps.Miasma = Math.Max(0f, ps.Miasma - M.TemporalGearBurn);
            server.SendCue(plr, "miasma-burn", 1f, 2f);
            server.SyncState(plr);
            return true;
        }

        // ------------------------------------------------------------------ decay

        private void OnTick(float dt)
        {
            if (!Cfg.Core.Enabled) return;
            double now = Api.World.Calendar.TotalHours;
            if (lastDecayHours <= double.MinValue)
            {
                // First tick after the world exists. Nothing has elapsed yet.
                lastDecayHours = now;
                lastPackRollHours = now;
                return;
            }

            double elapsed = now - lastDecayHours;
            if (elapsed <= 0) { lastDecayHours = now; return; }   // the clock just rewound
            lastDecayHours = now;

            bool storm = server.Anchors.IsTemporalStormActive();

            foreach (var plr in server.BlessedPlayers())
            {
                var ps = server.StateOf(plr);
                if (M.Enabled && !storm && ps.Miasma > 0)
                {
                    float rate = M.DecayPerHour;
                    if (ps.Miasma >= M.ClingThreshold) rate *= M.ClingDecayMultiplier;   // it clings
                    if (ps.AnchorAdvancedAtHours > double.MinValue &&
                        now - ps.AnchorAdvancedAtHours <= M.AnchorCleanseHours)
                        rate *= M.AnchorCleanseMultiplier;                                // progress cleanses

                    ps.Miasma = Math.Max(0f, ps.Miasma - (float)(rate * elapsed));
                }

                ApplyStabilityDrain(plr, ps, elapsed);
                WatchForSpentGear(plr, ps);
                RollPeripheralSilhouette(plr, ps, now);
                server.SyncState(plr);
            }

            RollMabeastPack(now);
        }

        private void ApplyStabilityDrain(IServerPlayer plr, PlayerState ps, double elapsedHours)
        {
            var tier = Tier(TierIndexFor(plr, ps));
            if (tier.StabilityDrainBonus <= 0) return;

            var attr = plr.Entity?.WatchedAttributes?.GetTreeAttribute("temporalStability");
            if (attr == null) return;
            double cur = attr.GetDouble("stability", 1.0);
            attr.SetDouble("stability", Math.Max(0, cur - tier.StabilityDrainBonus * elapsedHours * 0.05));
            plr.Entity.WatchedAttributes.MarkPathDirty("temporalStability");
        }

        /// <summary>
        /// Returning tears the local fabric: stability sags around the arrival for a
        /// while, and enough deaths in one place open a rift that never closes (§8).
        /// </summary>
        public void TearTemporalFabric(IServerPlayer plr)
        {
            if (!Cfg.Core.DeathScars) return;
            var ps = server.StateOf(plr);
            var last = ps.Ledger.Count > 0 ? ps.Ledger[ps.Ledger.Count - 1] : null;
            if (last == null) return;

            var scar = server.State.Scars.Find(s =>
                Math.Abs(s.X - last.X) < 16 && Math.Abs(s.Y - last.Y) < 16 && Math.Abs(s.Z - last.Z) < 16);

            if (scar == null)
            {
                scar = new ScarSite { X = last.X, Y = last.Y, Z = last.Z, Returns = 0, OpenedHours = double.MinValue };
                server.State.Scars.Add(scar);
            }
            scar.Returns++;

            if (scar.Returns >= Cfg.Core.DeathScarThreshold && scar.OpenedHours <= double.MinValue)
            {
                scar.OpenedHours = Api.World.Calendar.TotalHours;
                Api.Logger.Notification("[shinimodori] A death scar opened at {0},{1},{2} — {3} returns there.",
                    scar.X, scar.Y, scar.Z, scar.Returns);
                server.SendCue(plr, "scar", 1f, 3f);
            }
        }

        /// <summary>Local stability is depressed near fresh scars — the place is wrong now.</summary>
        private float OnGetTemporalStability(float stability, double x, double y, double z)
        {
            if (!Cfg.Core.DeathScars) return stability;
            foreach (var scar in server.State.Scars)
            {
                if (scar.OpenedHours <= double.MinValue) continue;
                double dx = scar.X - x, dy = scar.Y - y, dz = scar.Z - z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > 32 * 32) continue;
                float falloff = 1f - (float)(Math.Sqrt(d2) / 32.0);
                stability -= 0.15f * falloff;
            }
            return Math.Max(0f, stability);
        }

        // --------------------------------------------------------------- mabeasts

        private void OnEntitySpawn(Entity entity)
        {
            if (!Cfg.Core.Enabled || entity == null || entity is EntityPlayer) return;

            if (entity is EntityTrader)
            {
                if (entity.GetBehavior<ScentAversionBehavior>() == null)
                {
                    var b = new ScentAversionBehavior(entity);
                    b.Init(server);
                    entity.AddBehavior(b);
                }
                return;
            }

            if (!M.Enabled) return;
            if (entity.GetBehavior<EntityBehaviorTaskAI>() == null) return;
            if (entity.GetBehavior<ScentAggroBehavior>() != null) return;

            // Injecting a behaviour beats patching every creature's JSON, and it picks
            // up creatures added by other mods for free (§6.3).
            var beh = new ScentAggroBehavior(entity);
            beh.Init(server);
            entity.AddBehavior(beh);
        }

        private void RollMabeastPack(double nowHours)
        {
            if (!M.Enabled) return;
            if (nowHours - lastPackRollHours < 1.0) return;
            lastPackRollHours = nowHours;

            foreach (var plr in server.BlessedPlayers())
            {
                var ps = server.StateOf(plr);
                var tier = Tier(TierIndexFor(plr, ps));
                if (!tier.MabeastPacks) continue;

                bool night = Api.World.Calendar.HourOfDay < 5 || Api.World.Calendar.HourOfDay > 19;
                if (!night && !tier.MabeastsByDay) continue;

                if (AliveMabeasts(plr) >= M.MabeastMaxAlive) continue;
                if (Api.World.Rand.NextDouble() > M.MabeastPackChancePerHour) continue;

                SpawnPack(plr);
            }
        }

        private int AliveMabeasts(IServerPlayer plr)
        {
            if (!packs.TryGetValue(plr.PlayerUID, out var list)) return 0;
            list.RemoveAll(id => Api.World.GetEntityById(id)?.Alive != true);
            return list.Count;
        }

        /// <summary>
        /// 3–6 Ulgarm, out of line of sight, already knowing where you are (§6.4).
        /// </summary>
        public int SpawnPack(IServerPlayer plr)
        {
            var type = Api.World.GetEntityType(new AssetLocation("shinimodori:mabeast-ulgarm"));
            if (type == null)
            {
                server.Debug("mabeast entity type missing; pack event skipped");
                return 0;
            }

            int count = M.MabeastPackMin + Api.World.Rand.Next(Math.Max(1, M.MabeastPackMax - M.MabeastPackMin + 1));
            var centre = plr.Entity.Pos.XYZ;
            if (!packs.TryGetValue(plr.PlayerUID, out var list)) list = packs[plr.PlayerUID] = new List<long>();

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                var pos = FindSpawnPos(centre);
                if (pos == null) continue;

                var e = Api.ClassRegistry.CreateEntity(type);
                e.Pos.SetPos(pos);
                e.Pos.Yaw = (float)(Api.World.Rand.NextDouble() * Math.PI * 2);
                // The scent is yours alone: mark whose it is so they ignore everyone else.
                e.WatchedAttributes.SetString("sm:huntingUid", plr.PlayerUID);
                Api.World.SpawnEntity(e);
                list.Add(e.EntityId);
                spawned++;
            }

            if (spawned > 0)
            {
                Api.Logger.Notification("[shinimodori] {0} Ulgarm are hunting {1}.", spawned, plr.PlayerName);
                server.SendCue(plr, "mabeast-howl", 1f, 3f);
            }
            return spawned;
        }

        private Vec3d FindSpawnPos(Vec3d centre)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double angle = Api.World.Rand.NextDouble() * Math.PI * 2;
                double dist = M.MabeastSpawnMinDistance +
                              Api.World.Rand.NextDouble() * Math.Max(1, M.MabeastSpawnMaxDistance - M.MabeastSpawnMinDistance);
                int x = (int)(centre.X + Math.Cos(angle) * dist);
                int z = (int)(centre.Z + Math.Sin(angle) * dist);

                int? surface = Api.WorldManager.GetSurfacePosY(x, z);
                if (surface == null) continue;

                var pos = new BlockPos(x, surface.Value, z, 0);
                var block = Api.World.BlockAccessor.GetBlock(pos);
                if (block != null && block.Id != 0 && block.SideSolid.Any) continue;   // inside terrain
                return new Vec3d(x + 0.5, surface.Value, z + 0.5);
            }
            return null;
        }
    }
}
