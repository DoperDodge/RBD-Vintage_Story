namespace Shinimodori.Net
{
    /// <summary>
    /// Two channels (§13): low-frequency authoritative state, and fire-and-forget
    /// effect cues. Keeping them apart means a dropped cue can never desync state.
    /// </summary>
    public static class ChannelNames
    {
        public const string State = "shinimodori.state";
        public const string Fx = "shinimodori.fx";
    }
}
