# Class diagram — MusicPlayer core domain

_Category: [Architecture diagrams](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-26_

## Why this doc exists

Every other doc in this repo explains one feature at a time. This one steps back and shows how the handful of
long-lived singletons in `MusicPlayer` actually relate to each other — what owns what, and which class is
responsible for which slice of state. It's a map to orient with before diving into any single feature doc.

## Core domain classes

```mermaid
classDiagram
    class Player {
        <<singleton>>
        +PlaybackInformation PlaybackInformation
        +PlayerState State
        -IWavePlayer _audioPlayer
        -DynamicPlaylistSampleProvider _playlistProvider
        -PlaylistMode _mode
        -List~string~ _queuedPlaylist
        +Play(paths)
        +Pause()
        +PlayNext()
        +PlayPrevious()
        +SetAudioOutput(output)
        +SetEqualizerPreset(preset)
        +ToggleFavouriteAsync(path)
        +IncrementTimesPlayedAsync(path)
        +event PlaybackBroadcastStopRequested
        +event CurrentTrackChanged
    }

    class DynamicPlaylistSampleProvider {
        <<ISampleProvider>>
        -EnhancedAudioFileReader _currentProvider
        -BiQuadFilter[,] _equalizerFrequencyFilters
        +WaveFormat WaveFormat
        +Read(buffer, offset, count) int
        +HandleEndOfProviderReached()
        +EnsureWindowResampled()
        +AddProviders(paths)
        +RemoveProvider(path)
    }

    class EnhancedAudioFileReader {
        <<AudioFileReader>>
        +string OriginalFilePath
        +string ServerFilepath
    }

    class LibraryManager {
        <<singleton>>
        +MappingUpdate MappingUpdate
        +GetAlbumsAsync() Task~List_Album~
        +GetCacheLoadAlbumsAsync() Task~List_Album~
        +RefreshAlbumAsync(path)
        +ApplyLiveTrackUserDataAsync(album)
        +MigrateTrackUserDataIfNeededAsync()
        +event TrackUserDataChanged
    }

    class PlaylistManager {
        +GetPlaylistsAsync() Task~List_Playlist~
        +GetFavouritePlaylistAsync() Task~Playlist~
    }

    class LyricsManager {
        +GetTrackLyricsAsync(path) Task~Lyrics~
        +SaveManualLyricsAsync(path, lines)
        -ResolveFromEmbeddedTag(path)
        -ResolveFromLrcLib(path)
        -ResolveFromSidecarFile(path)
    }

    class MappingUpdate {
        +bool IsComplete
        +double CpuPercent
        +long MemoryBytes
        +string Error
        +event Changed
    }

    class MongoDbClient {
        <<singleton>>
        +albums
        +playlists
        +equalizer
        +mapping_statistics
        +player_settings
        +lyrics
        +trackUserData
        +EnsureCollectionsExistAsync()
    }

    class FileManager {
        <<singleton>>
        +Write~T~(T content)
        +Read~T~(guid) T
        -FileSystemWatcher
    }

    class Album {
        +ObjectId Id
        +Guid Guid
        +string Path
        +List~Track~ Tracks
    }

    class Track {
        +string Path
        +string Name
        +string Artist
        +TimeSpan Duration
        +bool IsFavourite
        +int TimesPlayed
    }

    Player --> DynamicPlaylistSampleProvider : owns one
    DynamicPlaylistSampleProvider --> EnhancedAudioFileReader : reads from current
    Player --> MongoDbClient : player_settings, trackUserData
    LibraryManager --> MongoDbClient : albums, trackUserData, equalizer
    LibraryManager --> FileManager : per-album JSON cache
    LibraryManager --> MappingUpdate : owns, updates live
    LibraryManager --> Album : produces
    Album --> Track : contains
    PlaylistManager --> MongoDbClient : playlists
    LyricsManager --> MongoDbClient : lyrics
    Player ..> LibraryManager : ToggleFavouriteAsync delegates to
```

## Reading this diagram

- **`Player`** and **`LibraryManager`** are the two big singletons — `Player` owns everything about the *current
  playback session* (queue, output device, live position), `LibraryManager` owns everything about *what's in the
  library* (mapping, favourites/play-counts, the JSON display cache). See [Storage map](../07-persistence-storage/storage-map.md)
  for exactly which of their fields live where.
- **`DynamicPlaylistSampleProvider`** is the only class that actually touches raw audio samples — see
  [Audio pipeline architecture](../01-playback-engine/audio-pipeline.md) for how it achieves gapless playback.
- **`MongoDbClient`** and **`FileManager`** are the two persistence gateways every domain class goes through;
  neither `Player` nor `LibraryManager` talks to Mongo or the filesystem through any other path.
- Neither `Player` nor `LibraryManager` references anything in `MusicServer` — the events on `Player`
  (`PlaybackBroadcastStopRequested`, `CurrentTrackChanged`) and `LibraryManager` (`TrackUserDataChanged`) are how
  state changes reach SignalR without a circular project reference — see
  [Event-bridge pattern](../05-realtime-sync/event-bridge-pattern.md).
- This diagram omits DTOs, hub classes, and `MusicServer`'s own broadcast-loop classes entirely — it's scoped to
  `MusicPlayer`'s core domain only. See [System architecture overview](system-architecture.md) for where
  `MusicServer` and the frontend fit around this core.
