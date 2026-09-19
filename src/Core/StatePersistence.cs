using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Vintagestory.API.Server;

namespace Shinimodori.Core
{
    /// <summary>
    /// Saves and loads the mod's world state (§4.10).
    ///
    /// The small state goes into the savegame so it travels with the world. The journal
    /// can be megabytes, so it is gzipped into the world's own ModData folder and only
    /// its hash is stored in the savegame — if the two disagree the journal is refused
    /// and the next return runs in SafeMode, which is the honest outcome.
    /// </summary>
    public static class StatePersistence
    {
        private const string JournalFile = "journal.bin.gz";

        private static string DataDir(ICoreServerAPI sapi)
        {
            string id = sapi.WorldManager.SaveGame.SavegameIdentifier;
            if (string.IsNullOrEmpty(id)) id = "unknown-world";
            return sapi.GetOrCreateDataPath(Path.Combine("ModData", id, "shinimodori"));
        }

        // --------------------------------------------------------------------- save

        public static void Save(ICoreServerAPI sapi, WorldState state, WorldJournal journal)
        {
            try
            {
                state.JournalHash = WriteJournal(sapi, journal);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[shinimodori] Could not write the journal: {0}. " +
                                  "The anchor is kept; the next return will run in SafeMode.", e.Message);
                state.JournalHash = "";
            }

            try
            {
                sapi.WorldManager.SaveGame.StoreData(WorldState.SaveKey, state.ToBytes());
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[shinimodori] Could not store world state: {0}", e);
            }
        }

        private static string WriteJournal(ICoreServerAPI sapi, WorldJournal journal)
        {
            if (journal == null) return "";

            byte[] raw;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                journal.Write(w);
                raw = ms.ToArray();
            }

            byte[] packed;
            using (var outMs = new MemoryStream())
            {
                using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, true)) gz.Write(raw, 0, raw.Length);
                packed = outMs.ToArray();
            }

            string dir = DataDir(sapi);
            string path = Path.Combine(dir, JournalFile);
            string tmp = path + ".tmp";

            // Write-then-move so a crash mid-save cannot leave a half-written journal
            // that would silently restore the world to nonsense.
            File.WriteAllBytes(tmp, packed);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            return Hash(packed);
        }

        // --------------------------------------------------------------------- load

        public static WorldState Load(ICoreServerAPI sapi)
        {
            try
            {
                var blob = sapi.WorldManager.SaveGame.GetData(WorldState.SaveKey);
                if (blob == null || blob.Length == 0) return new WorldState();
                return WorldState.FromBytes(blob);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[shinimodori] World state could not be read ({0}). " +
                                  "Starting fresh — existing worlds keep their blocks, but the anchor is lost.", e.Message);
                return new WorldState();
            }
        }

        /// <summary>
        /// Returns the stored journal, or null if it is missing, corrupt, or does not
        /// match the hash in the savegame. A null result means SafeMode on next return.
        /// </summary>
        public static WorldJournal LoadJournal(ICoreServerAPI sapi, WorldState state)
        {
            string path = Path.Combine(DataDir(sapi), JournalFile);
            try
            {
                if (!File.Exists(path))
                {
                    if (!string.IsNullOrEmpty(state.JournalHash))
                        sapi.Logger.Warning("[shinimodori] Journal file is missing; next return runs in SafeMode.");
                    return null;
                }

                byte[] packed = File.ReadAllBytes(path);
                if (!string.IsNullOrEmpty(state.JournalHash) && Hash(packed) != state.JournalHash)
                {
                    sapi.Logger.Warning("[shinimodori] Journal hash mismatch — it belongs to a different save. " +
                                        "Refusing it; next return runs in SafeMode.");
                    return null;
                }

                using (var inMs = new MemoryStream(packed))
                using (var gz = new GZipStream(inMs, CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    gz.CopyTo(outMs);
                    outMs.Position = 0;
                    using (var r = new BinaryReader(outMs)) return WorldJournal.Read(r);
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[shinimodori] Journal could not be read ({0}); next return runs in SafeMode.", e.Message);
                return null;
            }
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(data));
        }
    }
}
