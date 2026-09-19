using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shinimodori.Taboo
{
    /// <summary>One line of the reasoning behind a score, for tuning from user reports.</summary>
    public struct TabooSignal
    {
        public string Rule;
        public int Points;
        public string Evidence;
        public override string ToString() => $"{(Points >= 0 ? "+" : "")}{Points} {Rule}" +
                                             (string.IsNullOrEmpty(Evidence) ? "" : $" [{Evidence}]");
    }

    public class TabooScore
    {
        public int Total;
        public bool Triggered;
        public List<TabooSignal> Signals = new List<TabooSignal>();

        public string Explain()
        {
            var sb = new StringBuilder();
            sb.Append("score ").Append(Total).Append(Triggered ? " (TRIGGER)" : " (allowed)");
            foreach (var s in Signals) sb.Append("\n  ").Append(s);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Decides whether a piece of text describes Return by Death (§7.2).
    ///
    /// The distinction this has to get right is the whole tragedy of the story:
    /// **acting on foreknowledge is allowed; describing the ability is not.** "A wolf
    /// attacks at dusk, don't go out" must pass. "I died to that wolf before" must not.
    ///
    /// It scores signals rather than matching keywords, because a keyword list that
    /// eats ordinary chat would be worse than no mechanic at all. Every evaluation is
    /// explainable, and the corpus in tests/TabooCorpus.json pins the behaviour.
    ///
    /// Deliberately free of any Vintage Story dependency so it can be unit tested.
    /// </summary>
    public static class TabooDetector
    {
        /// <summary>Naming the thing outright. Nothing else needs to be present.</summary>
        private static readonly string[] StrongPhrases =
        {
            "return by death", "returnbydeath", "shinimodori", "死に戻り", "しにもどり",
            "i cant die", "i can not die", "i cannot die", "i am unable to die", "i never die",
            "i respawn", "i resurrect", "i revive when i die", "i come back when i die",
            "i go back when i die", "i reset when i die", "i restart when i die",
            "i keep coming back", "i always come back", "death sends me back",
            "dying sends me back", "when i die i go back", "when i die i wake up",
            "time loop", "time looping", "im in a loop", "i am in a loop", "im stuck in a loop",
            "i loop when i die", "groundhog day",
            "save point", "savepoint", "checkpoint", "check point",
            "i rewind", "i rewound time", "i reload a save", "quicksave",
            "my death is a checkpoint", "death is a checkpoint",
        };

        /// <summary>Claiming a memory of one's own death. Strong, but not conclusive alone.</summary>
        private static readonly string[] MemoryPhrases =
        {
            "i already died", "i have already died", "ive already died",
            "last time i died", "the last time i died", "when i died last",
            "this already happened", "this has already happened", "this happened already",
            "ive seen this before", "i have seen this before", "i saw this happen before",
            "i remember dying", "i remember my death", "i remember this happening",
            "we already did this", "ive done this already", "i have done this already",
            "this is my second time", "this is my third time", "ive lived this before",
        };

        /// <summary>Foreknowledge asserted about oneself. Permitted on its own — that is the point.</summary>
        private static readonly string[] ForeknowledgePhrases =
        {
            "i know what happens next", "i know whats about to happen", "i know how this ends",
            "trust me ive been here", "ive been here before", "i have been here before",
            "i know because ive seen it",
        };

        private static readonly HashSet<string> FirstPerson = new HashSet<string>
        { "i", "im", "ive", "id", "ill", "my", "me", "mine", "myself", "we", "weve", "our", "us" };

        /// <summary>
        /// First-person *subjects* only. Possessives are excluded on purpose: "my sheep
        /// died" is about the sheep, and a mechanic that cannot tell the difference
        /// would punish a player for losing livestock.
        /// </summary>
        private static readonly HashSet<string> FirstPersonSubject = new HashSet<string>
        { "i", "im", "ive", "id", "ill", "we", "weve" };

        /// <summary>Intransitive: the subject is the one who dies.</summary>
        private static readonly HashSet<string> DieWords = new HashSet<string>
        { "die", "died", "dies", "dying", "perish", "perished" };

        /// <summary>Transitive: "killed" only means *you* died with the right helper word.</summary>
        private static readonly HashSet<string> KillWords = new HashSet<string>
        { "kill", "killed", "kills", "murdered" };

        /// <summary>"I *got* killed", "I *was* killed" — the passive that means you died.</summary>
        private static readonly HashSet<string> PassiveHelpers = new HashSet<string>
        { "got", "get", "gets", "getting", "was", "were", "been", "being", "am", "is" };

        /// <summary>Objects that make a kill land on the speaker: "killed me", "kills us".</summary>
        private static readonly HashSet<string> SelfObjects = new HashSet<string> { "me", "us", "myself" };

        /// <summary>
        /// Words that turn a death into a *repetition*. "Earlier" and "yesterday" are
        /// deliberately absent: "I died earlier" is ordinary multiplayer chatter in any
        /// other game, and a mechanic that punishes it is a bug, not a feature.
        /// </summary>
        private static readonly HashSet<string> RepeatWords = new HashSet<string>
        { "again", "back", "loop", "looped", "looping", "rewind", "rewound", "restart", "restarted",
          "over", "before", "redo", "repeat", "repeated", "reset", "retry", "relive", "relived",
          "second", "third", "another" };

        /// <summary>Tokens either side of a death word that are searched for a repeat word.</summary>
        private const int PairWindow = 8;

        /// <summary>
        /// How far back a first-person subject may sit and still own the verb. Three is
        /// deliberate: it covers "I nearly died" and "I finally died" but not
        /// "I saw the wolf kill the sheep", where the speaker is the witness.
        /// </summary>
        private const int SubjectWindow = 3;

        public static TabooScore Evaluate(string text, int triggerScore = 3, string oocPrefix = "((")
        {
            var score = new TabooScore();
            if (string.IsNullOrWhiteSpace(text)) return score;

            string raw = text.Trim();
            string norm = Normalize(raw);
            var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            bool ooc = !string.IsNullOrEmpty(oocPrefix) && raw.StartsWith(oocPrefix, StringComparison.Ordinal);
            bool firstPerson = HasFirstPerson(tokens);

            bool strong = false, memory = false;

            foreach (var phrase in StrongPhrases)
            {
                if (!Contains(norm, phrase)) continue;
                Add(score, "strong phrase", 3, phrase);
                strong = true;
                break;
            }

            foreach (var phrase in MemoryPhrases)
            {
                if (!Contains(norm, phrase)) continue;
                // A claim to remember having already lived this is the ability, stated.
                Add(score, "memory of own death", 3, phrase);
                memory = true;
                break;
            }

            // The core inference: the speaker died, and says so as a repetition.
            // Either half alone is ordinary; together they describe the blessing.
            var pair = FindDeathRepeatPair(tokens);
            if (pair.found)
            {
                if (pair.self) Add(score, "own death + repetition", 3, pair.evidence);
                else Add(score, "third-party death + repetition", 1, pair.evidence);
            }

            foreach (var phrase in ForeknowledgePhrases)
            {
                if (!Contains(norm, phrase)) continue;
                // Foreknowledge is permitted. It is never enough on its own, and that
                // permission is the entire tragedy of the mechanic.
                Add(score, "foreknowledge claim", 1, phrase);
                break;
            }

            // An explicit out-of-character marker always wins. Players discussing the
            // mod itself must be safe, or the mechanic becomes a gag order.
            if (ooc) Add(score, "out-of-character prefix", -5, oocPrefix);

            if (!firstPerson && !strong && !memory) Add(score, "not about the speaker", -3, null);

            score.Total = 0;
            foreach (var s in score.Signals) score.Total += s.Points;
            score.Triggered = score.Total >= triggerScore;
            return score;
        }

        private static void Add(TabooScore score, string rule, int points, string evidence) =>
            score.Signals.Add(new TabooSignal { Rule = rule, Points = points, Evidence = evidence });

        private static bool HasFirstPerson(string[] tokens)
        {
            foreach (var t in tokens) if (FirstPerson.Contains(t)) return true;
            return false;
        }

        /// <summary>
        /// Finds a death word paired with a word that makes it a repetition, and works
        /// out whether the death was the speaker's own.
        /// </summary>
        private static (bool found, bool self, string evidence) FindDeathRepeatPair(string[] tokens)
        {
            bool anyFound = false;
            string anyEvidence = null;

            for (int i = 0; i < tokens.Length; i++)
            {
                bool isDie = DieWords.Contains(tokens[i]);
                bool isKill = KillWords.Contains(tokens[i]);
                if (!isDie && !isKill) continue;

                string repeat = FindRepeatNear(tokens, i);
                if (repeat == null) continue;

                anyFound = true;
                anyEvidence ??= tokens[i] + "\u2026" + repeat;

                if (IsSpeakersOwnDeath(tokens, i, isDie))
                    return (true, true, tokens[i] + "\u2026" + repeat);
            }

            return (anyFound, false, anyEvidence);
        }

        private static string FindRepeatNear(string[] tokens, int i)
        {
            int from = Math.Max(0, i - PairWindow);
            int to = Math.Min(tokens.Length - 1, i + PairWindow);
            for (int j = from; j <= to; j++)
            {
                if (j == i) continue;
                if (RepeatWords.Contains(tokens[j])) return tokens[j];
            }
            return null;
        }

        /// <summary>
        /// "I died" yes. "I killed the drifter" no — that is the drifter's death.
        /// "I got killed" and "it killed me" yes. "The wolves killed my chickens" no.
        /// </summary>
        private static bool IsSpeakersOwnDeath(string[] tokens, int i, bool intransitive)
        {
            if (intransitive)
            {
                for (int j = Math.Max(0, i - SubjectWindow); j < i; j++)
                    if (FirstPersonSubject.Contains(tokens[j])) return true;
                return false;
            }

            // Transitive kill: the speaker must be the object, or the verb passive.
            if (i + 1 < tokens.Length && SelfObjects.Contains(tokens[i + 1])) return true;

            bool passive = false;
            for (int j = Math.Max(0, i - 2); j < i; j++)
                if (PassiveHelpers.Contains(tokens[j])) { passive = true; break; }
            if (!passive) return false;

            for (int j = Math.Max(0, i - SubjectWindow - 1); j < i; j++)
                if (FirstPersonSubject.Contains(tokens[j])) return true;
            return false;
        }

        private static bool Contains(string normalizedHaystack, string normalizedNeedle) =>
            normalizedHaystack.Contains(normalizedNeedle, StringComparison.Ordinal);

        /// <summary>
        /// Lowercases, strips punctuation and apostrophes, and collapses whitespace, so
        /// "I've", "Ive" and "i ve" all read the same. Non-Latin scripts pass through
        /// unchanged so the Japanese spelling still matches.
        /// </summary>
        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append(' ');
            foreach (char c in text.ToLower(CultureInfo.InvariantCulture))
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '\'' || c == '’') { /* drop, so "i've" -> "ive" */ }
                else if (sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            if (sb[sb.Length - 1] != ' ') sb.Append(' ');
            return sb.ToString();
        }
    }
}
