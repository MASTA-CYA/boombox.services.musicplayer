using Microsoft.Extensions.Logging;
using MusicPlayer.Common;
using MusicPlayer.FileManagement.Interfaces;
using MusicPlayer.LibraryManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MusicPlayer.FileManagement
{
    public sealed class FileManager : IDisposable
    {
        private static readonly ILogger _logger = AppLogger.CreateLogger<FileManager>();

        private readonly string _appDataDirectory;
        private readonly FileSystemWatcher _watcher;
        private readonly Debouncer _debouncer;

        #region Singleton
        private static readonly Lazy<FileManager> _instance = new Lazy<FileManager>(() => new FileManager());
        private bool disposedValue;

        public static FileManager Instance { get => _instance.Value; }

        private FileManager()
        {
            _appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Boombox");
            Directory.CreateDirectory(_appDataDirectory);
            _debouncer = new Debouncer();

            _watcher = new FileSystemWatcher(Constants.LIBRARY_DIRECTORY)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                InternalBufferSize = 65536
            };
            _watcher.Created += OnCreated;
            _watcher.Deleted += OnDeleted;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }

        #endregion Singleton

        public void Write<T>(T content) where T : IFileWritable
        {
            var (id, json) = content.GetFileContent();
            File.WriteAllText(Path.Combine(_appDataDirectory, $"{id}.json"), json);
        }

        public void Write(string path, string content) => File.WriteAllText(Path.Combine(_appDataDirectory, path), content);

        public string Read(string path) => File.ReadAllText(path);

        // Dedicated pair for the trackUserData disaster-recovery backup (see MongoDbClient.BackupTrackUserDataAsync
        // and LibraryManager.MigrateTrackUserDataIfNeededAsync) - a fixed, single well-known file rather than the
        // generic Write(path, content)/Read(path) pair above, so callers never have to know or reconstruct the
        // AppData directory path themselves. Read returns null instead of throwing when the file doesn't exist yet
        // (e.g. genuinely first-ever run, before any backup has been written) - callers treat "no backup" as a
        // normal, expected case, not an error.
        public void WriteTrackUserDataBackup(string json) => File.WriteAllText(Path.Combine(_appDataDirectory, Constants.TRACK_USER_DATA_BACKUP_FILE), json);

        public string ReadTrackUserDataBackup()
        {
            var path = Path.Combine(_appDataDirectory, Constants.TRACK_USER_DATA_BACKUP_FILE);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public List<string> GetFilePaths(string pattern) => Directory.EnumerateFiles(_appDataDirectory, pattern, SearchOption.TopDirectoryOnly).ToList();

        public List<string> GetFilePaths(string pattern, string regexPattern)
        {
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
            return Directory.EnumerateFiles(_appDataDirectory, pattern, SearchOption.TopDirectoryOnly).Where(file => regex.IsMatch(Path.GetFileNameWithoutExtension(file))).ToList();
        }

        public List<string> GetResampledProviderFileNames() => Directory.EnumerateFiles(Constants.RESAMPLED_PROVIDERS_DIRECTORY).ToList();

        public void ClearDirectory(string path = null, string patern = null, string regexPattern = null)
        {
            try
            {
                if (!Directory.Exists(path ?? _appDataDirectory)) return;
                if (!string.IsNullOrWhiteSpace(patern))
                {
                    if (!string.IsNullOrWhiteSpace(regexPattern))
                    {
                        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                        var files = Directory.EnumerateFiles(path ?? _appDataDirectory, patern).Where(fileName => regex.IsMatch(Path.GetFileNameWithoutExtension(fileName)));
                        Parallel.ForEach(files, file => RemoveFile(file));
                        return;
                    }
                    Parallel.ForEach(Directory.EnumerateFiles(path ?? _appDataDirectory, patern), file => RemoveFile(file));
                }
                Parallel.ForEach(Directory.EnumerateFiles(path ?? _appDataDirectory), file => RemoveFile(file));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to clear directory {Path}", path ?? _appDataDirectory);
            }
        }

        public void RemoveDirectory(string path = null)
        {
            try
            {
                if (!Directory.Exists(path ?? _appDataDirectory)) return;

                foreach (var file in Directory.EnumerateFiles(path ?? _appDataDirectory))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path ?? _appDataDirectory, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to remove directory {Path}", path ?? _appDataDirectory);
            }
        }

        public void RemoveFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to remove file {Path}", path);
            }
        }

        public void RemoveFiles(string[] paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) return;

                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to remove file {Path}", path);
                }
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, Constants.RESAMPLED_PROVIDERS_DIRECTORY)) return;
            _debouncer.Debounce(LibraryManager.Instance.HandleNewAlbumAdded, milliseconds: 120000);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, Constants.RESAMPLED_PROVIDERS_DIRECTORY)) return;
            LibraryManager.Instance.HandleDeletedAlbums();
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            _logger.LogError(e.GetException(), "FileSystemWatcher raised an error");
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _watcher.Created -= OnCreated;
                    _watcher.Deleted -= OnDeleted;
                    _watcher.Error -= OnError;
                    _watcher.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}