using MusicPlayer.Common;
using MusicPlayer.FileManagment.Interfaces;
using MusicPlayer.LibraryManagement;
using MusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.FileManagment
{
    public sealed class FileManager : IDisposable
    {
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

        public List<string> GetFilePaths(string pattern) => Directory.EnumerateFiles(_appDataDirectory, pattern, SearchOption.AllDirectories).ToList();

        public List<string> GetResampledProviderFileNames() => Directory.EnumerateFiles(Constants.RESAMPLED_PROVIDERS_DIRECTORY).ToList();

        public void ClearDirectory(string path = null)
        {
            try
            {
                if (!Directory.Exists(path ?? _appDataDirectory)) return;
                Parallel.ForEach(Directory.EnumerateFiles(path ?? _appDataDirectory), file => RemoveFile(file));
            }
            catch (Exception ex)
            {
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "ClearDirectory",
                    Line = ex.ToString(),
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex.Message);
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
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "RemoveDirectory",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex.Message);
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
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "RemoveFile",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex.Message);
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
                    Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "RemoveFile",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    }));
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, Constants.RESAMPLED_PROVIDERS_DIRECTORY)) return;

            _debouncer.Debounce(() => LibraryManager.Instance.HandleCreatedAlbum(e.FullPath), milliseconds: 120000);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, Constants.RESAMPLED_PROVIDERS_DIRECTORY)) return;

            LibraryManager.Instance.HandleDeletedAlbum(e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
            {
                Severity = Severity.Error,
                Source = "ClearDirectory",
                Line = e.GetException().Message,
                TimeStamp = DateTime.Now,
                Exception = e.GetException()
            }));
            Console.WriteLine(e.GetException());
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