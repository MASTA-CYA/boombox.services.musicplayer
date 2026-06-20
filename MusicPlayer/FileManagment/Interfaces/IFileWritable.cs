namespace MusicPlayer.FileManagment.Interfaces
{
    public interface IFileWritable
    {
        (string id, string content) GetFileContent();
    }
}
