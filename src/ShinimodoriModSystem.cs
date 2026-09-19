using System;
using Shinimodori.Client;
using Shinimodori.Commands;
using Shinimodori.Compat;
using Shinimodori.Config;
using Shinimodori.Death;
using Shinimodori.Miasma;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Shinimodori
{
    /// <summary>
    /// The mod's entry point.
    ///
    /// Keeps the two sides strictly apart: the server decides everything, the client
    /// only renders and asks. Everything registered here is unregistered in Dispose so
    /// a world can be left and rejoined without leaking listeners or Harmony patches.
    /// </summary>
    public class ShinimodoriModSystem : ModSystem
    {
        public const string ConfigFile = "Shinimodori.json";
        public const string ModId = "shinimodori";

        public ShinimodoriConfig Config { get; private set; }
        public ShinimodoriServer Server { get; private set; }
        public ShinimodoriClient ClientSide { get; private set; }

        public override void StartPre(ICoreAPI api)
        {
            Config = ConfigPresets.LoadOrCreate(api, ConfigFile);
        }

        public override void Start(ICoreAPI api)
        {
            // Registered on both sides: the client needs the class to exist to
            // deserialise entities that carry it.
            api.RegisterEntityBehaviorClass(EntityBehaviorReturner.Name, typeof(EntityBehaviorReturner));
            api.RegisterEntityBehaviorClass(ScentAggroBehavior.Name, typeof(ScentAggroBehavior));
            api.RegisterEntityBehaviorClass(ScentAversionBehavior.Name, typeof(ScentAversionBehavior));

            if (Config == null) Config = ConfigPresets.LoadOrCreate(api, ConfigFile);
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            if (!Config.Core.Enabled)
            {
                sapi.Logger.Notification("[shinimodori] Disabled by config; the world stays ordinary.");
                return;
            }

            HarmonyPatches.Apply(sapi.Logger);

            Server = new ShinimodoriServer();
            Server.Start(sapi, Config);

            ShinimodoriCommands.Register(sapi, Server);

            if (!ShinimodoriBridge.BlockPatchesActive)
            {
                sapi.Logger.Warning("[shinimodori] Running with degraded journaling: changes made by the " +
                                    "world itself (fluids, crops, fire) will not be undone by a return.");
            }
            if (!ShinimodoriBridge.DiePatchActive)
            {
                sapi.Logger.Warning("[shinimodori] Running without the Die patch: deaths that bypass damage " +
                                    "(/kill, the void) may briefly show the vanilla death screen.");
            }
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            if (!Config.Core.Enabled) return;
            ClientSide = new ShinimodoriClient();
            ClientSide.Start(capi, Config);
        }

        /// <summary>Load after the vanilla content mods, so their entities and blocks exist.</summary>
        public override double ExecuteOrder() => 0.9;

        public override void Dispose()
        {
            try { ClientSide?.Dispose(); } catch (Exception) { }
            try { Server?.Dispose(); } catch (Exception) { }

            // Unpatching matters in single player, where the process outlives the world.
            HarmonyPatches.Unapply(null);

            ClientSide = null;
            Server = null;
        }
    }
}
