namespace MusicPlayer.FileManagement.Interfaces
{
    public interface IFileWritable
    {
        (string id, string content) GetFileContent();
    }
}
