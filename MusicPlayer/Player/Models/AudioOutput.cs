namespace MusicPlayer.Player.Models
{
    // Which physical device Player routes audio to. Speakers goes through NAudio's AsioOut (the Focusrite
    // interface); Headset goes through WasapiOut targeting the Corsair HS80, which has no ASIO driver of its own.
    // Plain int enum, same convention as PlaybackMode - Newtonsoft serializes it as a number and the Angular side
    // maps it to a display/icon state rather than the server sending a string.
    public enum AudioOutput
    {
        Speakers,
        Headset
    }
}
