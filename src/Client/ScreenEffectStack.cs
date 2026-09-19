using System;
using System.Collections.Generic;

namespace Shinimodori.Client
{
    /// <summary>The screen effects the mod can run, in the priority order of §12.3.</summary>
    public enum EffectKind
    {
        MiasmaAmbient = 10,
        DespairAmbient = 20,
        DeathRecall = 30,
        Breakdown = 50,
        TeaPartyTransition = 70,
        ReturnSequence = 90,
        TabooGrip = 100,
    }

    /// <summary>One running effect.</summary>
    public class ActiveEffect
    {
        public EffectKind Kind;
        public float Elapsed;
        public float Duration;
        public float Intensity = 1f;
        /// <summary>Sub-state, e.g. the grip stage or the return stage.</summary>
        public int Phase;
        public string Tag = "";
        /// <summary>Ambient effects never end on their own.</summary>
        public bool Persistent;

        public float Progress => Duration <= 0 ? 0f : Math.Min(1f, Elapsed / Duration);
        public bool Expired => !Persistent && Duration > 0 && Elapsed >= Duration;
    }

    /// <summary>
    /// Resolves overlapping effects (§12.3): the highest-priority effect fully
    /// suppresses everything below it, so the Void is never tinted by despair and a
    /// grip is never softened by an aura.
    /// </summary>
    public class ScreenEffectStack
    {
        private readonly Dictionary<EffectKind, ActiveEffect> effects = new Dictionary<EffectKind, ActiveEffect>();

        public ActiveEffect Begin(EffectKind kind, float duration, float intensity = 1f, int phase = 0, string tag = "")
        {
            var fx = new ActiveEffect
            {
                Kind = kind,
                Duration = duration,
                Intensity = intensity,
                Phase = phase,
                Tag = tag ?? "",
                Persistent = duration <= 0,
            };
            effects[kind] = fx;
            return fx;
        }

        public void End(EffectKind kind) => effects.Remove(kind);

        public ActiveEffect Get(EffectKind kind) => effects.TryGetValue(kind, out var fx) ? fx : null;

        public bool IsActive(EffectKind kind) => effects.ContainsKey(kind);

        /// <summary>The effect that owns the screen right now, or null.</summary>
        public ActiveEffect Top
        {
            get
            {
                ActiveEffect best = null;
                foreach (var fx in effects.Values)
                    if (best == null || fx.Kind > best.Kind) best = fx;
                return best;
            }
        }

        /// <summary>True when <paramref name="kind"/> is running and nothing above it is.</summary>
        public bool IsTop(EffectKind kind)
        {
            var top = Top;
            return top != null && top.Kind == kind;
        }

        /// <summary>Whether an effect may draw: only if nothing with higher priority is running.</summary>
        public ActiveEffect Visible(EffectKind kind)
        {
            var fx = Get(kind);
            if (fx == null) return null;
            var top = Top;
            return top != null && top.Kind > kind ? null : fx;
        }

        public void Update(float dt)
        {
            List<EffectKind> done = null;
            foreach (var kv in effects)
            {
                kv.Value.Elapsed += dt;
                if (!kv.Value.Expired) continue;
                (done ??= new List<EffectKind>()).Add(kv.Key);
            }
            if (done != null) foreach (var k in done) effects.Remove(k);
        }

        public void Clear() => effects.Clear();
    }
}
