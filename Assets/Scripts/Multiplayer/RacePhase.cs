namespace Multiplayer
{
    /// <summary>The stages a race moves through. The server owns the current phase; everyone else reacts to it.</summary>
    public enum RacePhase : byte
    {
        Lobby,      // waiting for the host to press Start
        Countdown,  // 3, 2, 1...
        Racing,     // flying the course
        Finale,     // winner fires lasers at the other ships while everyone watches
        Results     // end-of-race stats screen
    }
}
