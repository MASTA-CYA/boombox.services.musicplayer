namespace MusicPlayer.Common
{
    public class Constants
    {
        public const string LIBRARY_DIRECTORY = @"C:\Users\CYA\Music";
        public const string RESAMPLED_PROVIDERS_DIRECTORY = "C:\\Users\\CYA\\Music\\Resampled Providers";
        public const string JSON_FILE_PATTERN = "*.json";
        public const string GUUID_FILE_PATTERN = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
        // Disaster-recovery snapshot of the trackUserData collection - see MongoDbClient's BackupTrackUserDataAsync
        // and LibraryManager.MigrateTrackUserDataIfNeededAsync. Deliberately named/shaped nothing like the
        // per-album GUUID_FILE_PATTERN files so it's never mistaken for one by any of the Guid-keyed file logic.
        public const string TRACK_USER_DATA_BACKUP_FILE = "trackUserData.backup.json";
    }
}
