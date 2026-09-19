using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Shinimodori.Death
{
    /// <summary>
    /// How a return was caused (§5.3). Several systems read this: the arrival sound,
    /// the sensory afterimage, Echidna's dialogue, and the ledger.
    /// </summary>
    public enum DeathCause
    {
        Unknown = 0,
        Wolf = 1,
        Bear = 2,
        Drifter = 3,
        Locust = 4,
        Bell = 5,
        Fall = 6,
        Drowning = 7,
        Starvation = 8,
        Hypothermia = 9,
        Heat = 10,
        TemporalInstability = 11,
        Fire = 12,
        Suffocation = 13,
        Explosion = 14,
        Player = 15,
        Taboo = 16,
        Voluntary = 17,
        Mabeast = 18,
        Creature = 19,
        Poison = 20,
        Bleeding = 21,
        Void = 22,
    }

    /// <summary>Which sensory afterimage the arrival plays (§9.1).</summary>
    public enum Afterimage { None = 0, Cold = 1, Heat = 2, Drowning = 3 }

    public static class DeathCauses
    {
        /// <summary>
        /// Maps a Vintage Story damage source onto the mod's vocabulary. Named creatures
        /// are recognised by code so the ledger can say "the wolves" rather than "an entity".
        /// </summary>
        public static DeathCause From(DamageSource src, out string killerName)
        {
            killerName = "";
            if (src == null) return DeathCause.Unknown;

            var cause = src.GetCauseEntity() ?? src.SourceEntity;
            if (cause != null)
            {
                string code = cause.Code?.Path ?? "";
                killerName = FriendlyName(cause);

                if (cause is EntityPlayer) return DeathCause.Player;
                if (code.StartsWith("mabeast", StringComparison.OrdinalIgnoreCase)) return DeathCause.Mabeast;
                if (code.Contains("wolf")) return DeathCause.Wolf;
                if (code.Contains("bear")) return DeathCause.Bear;
                if (code.Contains("drifter")) return DeathCause.Drifter;
                if (code.Contains("locust")) return DeathCause.Locust;
                if (code.Contains("bell")) return DeathCause.Bell;
            }

            switch (src.Source)
            {
                case EnumDamageSource.Fall: return DeathCause.Fall;
                case EnumDamageSource.Drown: return DeathCause.Drowning;
                case EnumDamageSource.Void: return DeathCause.Void;
                case EnumDamageSource.Explosion: return DeathCause.Explosion;
                case EnumDamageSource.Suicide: return DeathCause.Voluntary;
                case EnumDamageSource.Player: return DeathCause.Player;
            }

            switch (src.Type)
            {
                case EnumDamageType.Gravity: return DeathCause.Fall;
                case EnumDamageType.Fire: return DeathCause.Fire;
                case EnumDamageType.Heat: return DeathCause.Heat;
                case EnumDamageType.Frost: return DeathCause.Hypothermia;
                case EnumDamageType.Hunger: return DeathCause.Starvation;
                case EnumDamageType.Suffocation: return DeathCause.Suffocation;
                case EnumDamageType.Poison: return DeathCause.Poison;
                case EnumDamageType.Crushing: return DeathCause.Suffocation;
                case EnumDamageType.Injury: return DeathCause.TemporalInstability;
            }

            if (cause != null) return DeathCause.Creature;
            return DeathCause.Unknown;
        }

        public static string FriendlyName(Entity e)
        {
            if (e == null) return "";
            if (e is EntityPlayer ep) return ep.Player?.PlayerName ?? "someone";
            string code = e.Code?.Path ?? "";
            int dash = code.IndexOf('-');
            return dash > 0 ? code.Substring(0, dash) : code;
        }

        public static Afterimage AfterimageFor(DeathCause cause)
        {
            switch (cause)
            {
                case DeathCause.Hypothermia: return Afterimage.Cold;
                case DeathCause.Fire:
                case DeathCause.Heat: return Afterimage.Heat;
                case DeathCause.Drowning: return Afterimage.Drowning;
                default: return Afterimage.None;
            }
        }

        /// <summary>Lang key for the ledger and Echidna's lines.</summary>
        public static string LangKey(DeathCause cause) =>
            "shinimodori:deathcause-" + cause.ToString().ToLowerInvariant();
    }
}
