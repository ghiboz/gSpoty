using SpotifyAPI.Web;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

/// <summary>
/// Listener class for listening to the current playing context on Spotify
/// and providing callbacks related to the context
/// </summary>
public class SpotifyPlayerListener : SpotifyServiceListener
{
    /// <summary>
    /// Amount of milliseconds for the first poll; later polls adapt to the remaining song time
    /// </summary>
    public float UpdateFrequencyMS = 100;

    // Spotify search endpoint accepts at most 10 results per type
    private const int SearchLimit = 10;

    /// <summary>
    /// Triggered when a new Track or Episode is playing in the player
    /// </summary>
    public event Action<IPlayableItem> OnPlayingItemChanged;
    public event Action<int> OnSpotifyUpdate;
    public event Action<bool> OnSongAddedToPlayList;
    public event Action<string> OnError;
    public event Action<double> OnSongPlaying;

    // Current connected spotify client
    private SpotifyClient _client;

    // Current playing item
    private IPlayableItem _currentItem;
    // Id of the playlist being played, null when playing from an album, artist, etc.
    private string _contextPlaylistId;
    // Is the internal update loop running?
    private bool _isInvoking = false;
    private System.Timers.Timer tmrUpdate;
    private int cntUpdate = 0;
    private readonly string playlist;
    private readonly string playlistNew;

    // Original album id -> resolved "real" album
    private readonly ConcurrentDictionary<string, FullAlbum> _albumCache = new ConcurrentDictionary<string, FullAlbum>();

    public SpotifyPlayerListener(string playlist, string playlistNew)
    {
        this.playlist = playlist;
        this.playlistNew = playlistNew;
    }

    public IPlayableItem CurrentItem => _currentItem;

    /// <summary>
    /// Playlist the current song is removed from: the one being played, or PlayListNew as fallback
    /// </summary>
    private string SourcePlaylist =>
        !string.IsNullOrEmpty(_contextPlaylistId) && _contextPlaylistId != playlist ? _contextPlaylistId : playlistNew;

    protected override void OnSpotifyConnectionChanged(SpotifyClient client)
    {
        base.OnSpotifyConnectionChanged(client);

        _client = client;
        // Start internal update loop
        if (_client != null && UpdateFrequencyMS > 0)
        {
            if (SpotifyService.Instance.AreScopesAuthorized(Scopes.UserReadPlaybackState))
            {
                if (tmrUpdate == null)
                {
                    // One-shot timer, re-armed after each fetch so polls never overlap
                    tmrUpdate = new System.Timers.Timer(UpdateFrequencyMS) { AutoReset = false };
                    tmrUpdate.Elapsed += async (s, e) => await FetchLoop();
                }
                tmrUpdate.Start();
                _isInvoking = true;
            }
            else
            {
                Console.WriteLine($"Not authorized to access '{Scopes.UserReadPlaybackState}'");
            }
        }
        else if (_client == null && _isInvoking)
        {
            tmrUpdate.Stop();
            _isInvoking = false;

            // Invoke playing item changed, no more client, no more context
            SetCurrentItem(null);
        }
    }

    private async Task FetchLoop()
    {
        int nextCheck = 1000;
        try
        {
            nextCheck = await FetchLatestPlayer();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }

        if (_isInvoking)
        {
            tmrUpdate.Interval = nextCheck;
            tmrUpdate.Start();
        }
    }

    public async Task RemoveSongFromPlaylist()
    {
        var source = SourcePlaylist;
        if (_currentItem is not FullTrack track || string.IsNullOrEmpty(source))
            return;

        try
        {
            await RemoveFromPlaylist(source, track.Uri);
        }
        catch (Exception ex)
        {
            ReportError("Remove", ex);
        }
    }

    private Task RemoveFromPlaylist(string playlistId, string uri)
    {
        return _client.Playlists.RemovePlaylistItems(playlistId, new PlaylistRemoveItemsRequestV2
        {
            Items = new List<PlaylistRemoveItemsRequestV2.Item>
            {
                new PlaylistRemoveItemsRequestV2.Item { Uri = uri }
            }
        });
    }

    /// <summary>
    /// Finds the oldest studio album of the artist containing a track whose name starts with startTrack.
    /// Albums are checked from the oldest, so it stops at the first match.
    /// </summary>
    private async Task<SimpleAlbum> GetOldestAlbumWithTrackStartingWith(string artistId, string startTrack)
    {
        var albumsPage = await _client.Artists.GetAlbums(artistId, new ArtistsAlbumsRequest
        {
            IncludeGroupsParam = ArtistsAlbumsRequest.IncludeGroups.Album,
            Limit = 50
        });

        var allAlbums = new List<SimpleAlbum>();
        await foreach (var album in _client.Paginate(albumsPage))
        {
            allAlbums.Add(album);
        }

        // Release dates are "YYYY", "YYYY-MM" or "YYYY-MM-DD", sortable as strings
        foreach (var album in allAlbums.Where(a => a.AlbumType == "album").OrderBy(a => a.ReleaseDate))
        {
            var tracksPage = await _client.Albums.GetTracks(album.Id, new AlbumTracksRequest { Limit = 50 });
            await foreach (var track in _client.Paginate(tracksPage))
            {
                if (track.Name.StartsWith(startTrack, StringComparison.OrdinalIgnoreCase))
                    return album;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the original studio album of a track (instead of a single or compilation), cached per album id
    /// </summary>
    public async Task<FullAlbum> GetRealAlbum(FullTrack track)
    {
        if (_albumCache.TryGetValue(track.Album.Id, out var cached))
            return cached;

        var album = await ResolveRealAlbum(track);
        if (album != null)
            _albumCache[track.Album.Id] = album;
        return album;
    }

    private async Task<FullAlbum> ResolveRealAlbum(FullTrack track)
    {
        if (track.Album.AlbumType == "album")
        {
            return await _client.Albums.Get(track.Album.Id);
        }

        var search = await _client.Search.Item(new SearchRequest(SearchRequest.Types.Album, track.Album.Name)
        {
            Limit = SearchLimit
        });

        var matchingAlbum = search.Albums?.Items?
            .Where(a => a.AlbumType == "album") // esclude "single" e "compilation"
            .FirstOrDefault(a => a.Artists.Any(artist =>
                track.Artists.Any(trackArtist =>
                    trackArtist.Id == artist.Id))); // confronta per ID, più affidabile del nome

        if (matchingAlbum != null)
        {
            // Ritorna il FullAlbum con tutte le immagini corrette
            return await _client.Albums.Get(matchingAlbum.Id);
        }

        var oldestAlbum = await GetOldestAlbumContainingTrack(track);
        if (oldestAlbum != null)
        {
            return oldestAlbum;
        }

        // last try
        var realTrackName = Regex.Replace(track.Name, @"[\(\[\{][^\)\]\}]*[\)\]\}]", "").Trim();
        var albumWithTrack = await GetOldestAlbumWithTrackStartingWith(track.Artists[0].Id, realTrackName);
        return await _client.Albums.Get(albumWithTrack?.Id ?? track.Album.Id);
    }

    public async Task<FullAlbum> GetOldestAlbumContainingTrack(FullTrack track)
    {
        // Cerca la traccia per titolo e artista
        var artistName = track.Artists.FirstOrDefault()?.Name ?? "";
        var search = await _client.Search.Item(new SearchRequest(SearchRequest.Types.Track, $"{track.Name} artist:{artistName}")
        {
            Limit = SearchLimit
        });

        var items = search.Tracks?.Items;
        if (items == null || items.Count == 0) return null;

        // Filtra solo le tracce che matchano esattamente per ID artista e nome
        var sameSong = items
            .Where(t => t.Name.Equals(track.Name, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Artists.Any(a => track.Artists.Any(ta => ta.Id == a.Id)))
            .ToList();

        var matchingTracks = sameSong
            .Where(t => t.Artists.Count == 1)
            .Where(t => t.Album.AlbumType == "album") // solo album, no single/compilation
            .ToList();

        if (matchingTracks.Count == 0)
        {
            matchingTracks = sameSong
                .Where(t => t.Album.Images.Any(img => img.Width == img.Height)) // solo album con copertina quadrata
                .ToList();

            if (matchingTracks.Count == 0) return null;
        }

        // Trova l'album più vecchio confrontando le date di uscita
        var oldestTrack = matchingTracks
            .OrderBy(t => t.Album.ReleaseDate) // formato "YYYY-MM-DD" o "YYYY", ordinabile come stringa
            .First();

        return await _client.Albums.Get(oldestTrack.Album.Id);
    }

    /// <summary>
    /// Adds the current song to the main playlist (if not already there) and removes it from the "new" playlist
    /// </summary>
    public async Task AddSongToPlaylist()
    {
        if (_currentItem is not FullTrack track)
            return;

        bool addSong = true;
        try
        {
            var firstPage = await _client.Playlists.GetPlaylistItems(playlist, new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track)
            {
                Limit = 100
            });

            await foreach (var songInPlaylist in _client.Paginate(firstPage))
            {
                if (songInPlaylist.Track is FullTrack song
                    && (song.Uri == track.Uri
                        || (song.Album.Name == track.Album.Name
                            && song.Name == track.Name
                            && song.Artists.FirstOrDefault()?.Name == track.Artists.FirstOrDefault()?.Name)))
                {
                    addSong = false;
                    break;
                }
            }

            if (addSong)
            {
                await _client.Playlists.AddPlaylistItems(playlist, new PlaylistAddItemsRequest(new List<string> { track.Uri }));
            }
        }
        catch (Exception ex)
        {
            ReportError("Add", ex);
            return;
        }

        // Move semantics: remove from the source even if the song was already in the destination
        var source = SourcePlaylist;
        if (!string.IsNullOrEmpty(source))
        {
            try
            {
                await RemoveFromPlaylist(source, track.Uri);
            }
            catch (Exception ex)
            {
                ReportError("Remove", ex);
                return;
            }
        }

        OnSongAddedToPlayList?.Invoke(addSong);
    }

    private void ReportError(string action, Exception ex)
    {
        var message = ex is APIException apiEx && apiEx.Response != null
            ? $"{action}: {(int)apiEx.Response.StatusCode} {apiEx.Message}"
            : $"{action}: {ex.Message}";
        Console.WriteLine(message);
        OnError?.Invoke(message);
    }

    private void SetCurrentItem(IPlayableItem item)
    {
        _currentItem = item;
        OnPlayingItemChanged?.Invoke(item);
    }

    /// <summary>
    /// Polls the player and returns the delay in ms before the next poll
    /// </summary>
    private async Task<int> FetchLatestPlayer()
    {
        if (_client == null)
        {
            // If no client but has a previous item, invoke event
            if (_currentItem != null)
                SetCurrentItem(null);
            return 1000;
        }

        CurrentlyPlayingContext newContext;
        try
        {
            newContext = await _client.Player.GetCurrentPlayback();
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.WriteLine(ex.ToString());
#endif
            return 2000;
        }

        OnSpotifyUpdate?.Invoke(++cntUpdate);

        _contextPlaylistId = newContext?.Context?.Type == "playlist"
            ? newContext.Context.Uri?.Split(':').Last()
            : null;

        switch (newContext?.Item)
        {
            case FullTrack currentTrack:
            {
                var duration = currentTrack.DurationMs;
                var progress = newContext.ProgressMs;
                OnSongPlaying?.Invoke(duration > 0 ? progress / (double)duration : 0);

                if (_currentItem is not FullTrack lastTrack
                    || lastTrack.Name != currentTrack.Name
                    || S4UUtility.HasArtistsChanged(lastTrack.Artists, currentTrack.Artists))
                {
                    Console.WriteLine($"-> {S4UUtility.GetTrackString(currentTrack)}");
                    SetCurrentItem(currentTrack);
                }

                // Poll more often near the end of the song
                return Math.Clamp((duration - progress) / 2, 500, 10000);
            }

            case FullEpisode currentEpisode:
                if (_currentItem is not FullEpisode lastEpisode
                    || lastEpisode.Name != currentEpisode.Name
                    || lastEpisode.Show?.Name != currentEpisode.Show?.Name)
                {
                    Console.WriteLine($"-> {currentEpisode.Show?.Name} {currentEpisode.Name}");
                    SetCurrentItem(currentEpisode);
                }
                return 5000;

            default:
                // No context or null current playing item
                if (_currentItem != null)
                {
                    Console.WriteLine("-> nothing playing");
                    SetCurrentItem(null);
                }
                return 5000;
        }
    }
}
