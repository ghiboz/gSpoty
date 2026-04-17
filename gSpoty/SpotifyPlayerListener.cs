using SpotifyAPI.Web;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;

/// <summary>
/// Listener class for listening to the current playing context on Spotify 
/// and providing callbacks related to the context
/// </summary>
public class SpotifyPlayerListener : SpotifyServiceListener
{
    /// <summary>
    /// Amount of milliseconds for the internal player updater to poll at
    /// </summary>
    public float UpdateFrequencyMS = 100;

    /// <summary>
    /// Triggered when a new Track or Episode is playing in the player
    /// </summary>
    public event Action<IPlayableItem> OnPlayingItemChanged;
    public event Action<int> OnSpotifyUpdate;
    public event Action<bool> OnSongAddedToPlayList;
    public event Action<double> OnSongPlaying;

    // Current connected spotify client
    private SpotifyClient _client;

    // The last retrieved context from API
    private CurrentlyPlayingContext _currentContext;
    // Current playing item within context
    private IPlayableItem _currentItem;
    // Is the internal update loop being invoked?
    private bool _isInvoking = false;
    private System.Timers.Timer tmrUpdate;
    private int cntUpdate = 0;
    private string playlist;
    private string playlistNew;
    private IPlayableItem currentItem;

    public SpotifyPlayerListener(string playlist, string playlistNew)
    {
        this.playlist = playlist;
        this.playlistNew = playlistNew;
        this.OnPlayingItemChanged += PlayingItemChanged;
    }

    protected override void OnSpotifyConnectionChanged(SpotifyClient client)
    {
        base.OnSpotifyConnectionChanged(client);

        _client = client;
        // Start internal update loop
        if (_client != null && UpdateFrequencyMS > 0)
        {
            if (SpotifyService.Instance.AreScopesAuthorized(Scopes.UserReadPlaybackState))
            {
                //!!! InvokeRepeating(nameof(FetchLatestPlayer), 0, UpdateFrequencyMS / 1000);
                tmrUpdate = SetIntervalThread(FetchLatestPlayer, UpdateFrequencyMS);

                ///FetchLatestPlayer();
                _isInvoking = true;
            }
            else
            {
                Console.WriteLine($"Not authorized to access '{Scopes.UserReadPlaybackState}'");
            }
        }
        else if (_client == null && _isInvoking)
        {
            //!!!CancelInvoke(nameof(FetchLatestPlayer));
            tmrUpdate.Stop();
            _isInvoking = false;

            // Invoke playing item changed, no more client, no more context
            OnPlayingItemChanged?.Invoke(null);
        }
    }

    public async void RemoveSongFromPlaylist()
    {
        var track = currentItem as FullTrack;
        var itemRemove = new PlaylistRemoveItemsRequest();
        // remove the item to the playlist
        var itemsToRemove = new PlaylistRemoveItemsRequest
        {
            Tracks = new List<PlaylistRemoveItemsRequest.Item>
                {
                    new PlaylistRemoveItemsRequest.Item
                    {
                        Uri = track.Uri
                    }
                }
        };
        await _client.Playlists.RemoveItems(playlistNew, itemsToRemove);
    }

    public async Task<FullAlbum> GetAlbum(string albumId)
    {
        var album = await _client.Albums.Get(albumId);
        return album;
    }

    public async Task<List<SimpleAlbum>> GetAlbumsWithTrackStartingWith(string artistId, string startTrack)
    {
        var matchingAlbums = new List<SimpleAlbum>();

        // 1. Recupera tutti gli album dell'artista
        var albumsPage = await _client.Artists.GetAlbums(artistId, new ArtistsAlbumsRequest
        {
            IncludeGroupsParam = ArtistsAlbumsRequest.IncludeGroups.Album,
            Limit = 50
        });

        var allAlbums = new List<SimpleAlbum>();

        // Pagina attraverso tutti gli album
        await foreach (var album in _client.Paginate(albumsPage))
        {
            allAlbums.Add(album);
        }

        // 2. Per ogni album, controlla se contiene una traccia che inizia con startTrack
        foreach (var album in allAlbums)
        {
            var tracksPage = await _client.Albums.GetTracks(album.Id, new AlbumTracksRequest
            {
                Limit = 50
            });

            bool found = false;

            await foreach (var track in _client.Paginate(tracksPage))
            {
                if (track.Name.StartsWith(startTrack, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (found)
                matchingAlbums.Add(album);
        }

        return matchingAlbums;
    }

    public async Task<FullAlbum?> GetRealAlbum(FullTrack track)
    {
        if (track.Album.AlbumType == "album")
        {
            return await _client.Albums.Get(track.Album.Id);
        }

        var search = await _client.Search.Item(new SearchRequest(SearchRequest.Types.Album, track.Album.Name)
        {
            Limit = 50
        });

        var matchingAlbum = search.Albums.Items?
            .Where(a => a.AlbumType == "album") // esclude "single" e "compilation"
            .FirstOrDefault(a => a.Artists.Any(artist => 
                track.Artists.Any(trackArtist => 
                    trackArtist.Id == artist.Id))); // confronta per ID, più affidabile del nome

        if (matchingAlbum == null)
        {
            var oldest_album = await GetOldestAlbumContainingTrack(track);
            if (oldest_album != null)
            {
                return oldest_album;
            }
            else
            {
                // last try
                var real_track_name = Regex.Replace(track.Name, @"[\(\[\{][^\)\]\}]*[\)\]\}]", "").Trim();
                var albums = GetAlbumsWithTrackStartingWith(track.Artists[0].Id, real_track_name).GetAwaiter().GetResult();

                var oldestAlbum = albums
                    .Where(a => a.AlbumType == "album")
                    .OrderBy(t => t.ReleaseDate)
                    .First();

                if (oldestAlbum != null)
                {
                    return await _client.Albums.Get(oldestAlbum.Id);
                }

                return await _client.Albums.Get(track.Album.Id);
            }
        }
        // Ritorna il FullAlbum con tutte le immagini corrette
        return await _client.Albums.Get(matchingAlbum.Id);
    }

    public async Task<FullAlbum?> GetOldestAlbumContainingTrack(FullTrack track)
    {
        // Cerca la traccia per titolo e artista
        var artistName = track.Artists.FirstOrDefault()?.Name ?? "";
        var search = await _client.Search.Item(new SearchRequest(SearchRequest.Types.Track, $"{track.Name} artist:{artistName}")
        {
            Limit = 50
        });

        if (search.Tracks.Items == null || !search.Tracks.Items.Any()) return null;

        // Filtra solo le tracce che matchano esattamente per ID artista e nome
        var matchingTracks = search.Tracks.Items
            .Where(t => t.Name.Equals(track.Name, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Artists.Any(a => track.Artists.Any(ta => ta.Id == a.Id)))
            .Where(t => t.Artists.Count == 1)
            .Where(t => t.Album.AlbumType == "album") // solo album, no single/compilation
            .ToList();

        if (!matchingTracks.Any())
        {
            matchingTracks = search.Tracks.Items
            .Where(t => t.Name.Equals(track.Name, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Artists.Any(a => track.Artists.Any(ta => ta.Id == a.Id)))
            //.Where(t => t.Album.AlbumType == "album")
            .Where(t => t.Album.Images.Any(img => img.Width != null && img.Height != null && img.Width == img.Height)) // solo album con copertina quadrata
            .ToList();

            if (!matchingTracks.Any()) return null;
        }

        // Trova l'album più vecchio confrontando le date di uscita
        var oldestTrack = matchingTracks
            .OrderBy(t => t.Album.ReleaseDate) // formato "YYYY-MM-DD" o "YYYY", ordinabile come stringa
            .First();

        return await _client.Albums.Get(oldestTrack.Album.Id);
    }

    public async Task<FullTrack> GetAlbumTrack(FullTrack track)
    {
        var search = await _client.Search.Item(new SearchRequest(SearchRequest.Types.Track, track.Name)
        {
            Limit = 10
        });
        var correctTrack = search.Tracks.Items
        .Where(t =>
            t.Artists.First().Id == track.Artists.First().Id &&
            t.Album.AlbumType == "album").OrderBy(o => o.Album.ReleaseDate) // <-- chiave!
        .FirstOrDefault();

        if (correctTrack == null)
        {
            return track;
        }
        return correctTrack;
    }

    public async void AddSongToPlaylist()
    {
        var track = currentItem as FullTrack;
        var item = new PlaylistAddItemsRequest(new List<string>() { track.Uri });
        var itemRemove = new PlaylistRemoveItemsRequest();
       

        bool addSong = true;
        var pl = await _client.Playlists.Get(playlist);
        var plNew = await _client.Playlists.Get(playlistNew);

        int totalSongs = pl.Tracks.Total.Value;
        int totalSongsNew = plNew.Tracks.Total.Value;

        int loops = Convert.ToInt32(Math.Ceiling(totalSongs / (decimal)pl.Tracks.Limit.Value));
        for (int i = 0; i < loops; i++)
        {
            var req = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track);
            req.Limit = pl.Tracks.Limit.Value;
            req.Offset = i * req.Limit;
            var q = req.BuildQueryParams();

            var playlistItems = await _client.Playlists.GetItems(playlist, req);
            foreach (var songInPlaylist in playlistItems.Items)
            {
                var song = songInPlaylist.Track as FullTrack;
                if (song.Album.Name.Equals(track.Album.Name)
                    && song.Name.Equals(track.Name)
                    && song.Artists[0].Name.Equals(track.Artists[0].Name))
                {
                    addSong = false;
                    i = loops;
                    break;
                }
            }
        }

        if (addSong)
        {
            await _client.Playlists.AddItems(playlist, item);

            // remove the item to the playlist
            var itemsToRemove = new PlaylistRemoveItemsRequest
            {
                Tracks = new List<PlaylistRemoveItemsRequest.Item>
                {
                    new PlaylistRemoveItemsRequest.Item
                    {
                        Uri = track.Uri
                    }
                }
            };
            await _client.Playlists.RemoveItems(playlistNew, itemsToRemove);

        }
        OnSongAddedToPlayList?.Invoke(addSong);
    }

    public static System.Timers.Timer SetIntervalThread(Action Act, float interval)
    {
        var tmr = new System.Timers.Timer();
        tmr.Elapsed += (sender, args) => Act();
        tmr.AutoReset = true;
        tmr.Interval = interval;
        tmr.Start();

        return tmr;
    }

    protected virtual void PlayingItemChanged(IPlayableItem item)
    {
        // Override me.
        currentItem = item;
    }

    private int MaxMin(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private async void FetchLatestPlayer()
    {
        if (_client != null)
        {
            // get the current context on this run
            CurrentlyPlayingContext newContext = null;

            try
            {
                newContext = await _client.Player.GetCurrentPlayback();

            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex.ToString());
#endif
                return;
            }

            // Check if not null
            if (newContext != null && newContext.Item != null)
            {
                // Check and cast the item to the correct type
                if (newContext.Item.Type == ItemType.Track)
                {
                    FullTrack currentTrack = newContext.Item as FullTrack;
                    var duration = currentTrack.DurationMs;
                    var progress = newContext.ProgressMs;
                    var remain = duration - progress;
                    var nextCheck = MaxMin(remain / 2, 500, 10000);

                    OnSongPlaying?.Invoke(progress / (double)duration);

                    tmrUpdate.Interval = nextCheck;
                    OnSpotifyUpdate?.Invoke(++cntUpdate);

                    // No previous track or previous item was different type 
                    if (_currentItem == null || (_currentItem != null && _currentItem is FullEpisode episode))
                    {
                        //Console.WriteLine($"No prev track or new type | -> '{S4UUtility.GetTrackString(currentTrack)}'");
                        Console.WriteLine($"-> {S4UUtility.GetTrackString(currentTrack)}");
                        _currentItem = currentTrack;
                        OnPlayingItemChanged?.Invoke(_currentItem);
                    }
                    else if (_currentItem != null && _currentItem is FullTrack lastTrack)
                    {
                        // Check if track name & artists aren't the same
                        if (lastTrack.Name != currentTrack.Name || S4UUtility.HasArtistsChanged(lastTrack.Artists, currentTrack.Artists))
                        {
                            //Console.WriteLine($"Track to new Track | '{S4UUtility.GetTrackString(lastTrack)}' -> '{S4UUtility.GetTrackString(currentTrack)}'");
                            Console.WriteLine($"-> {S4UUtility.GetTrackString(currentTrack)}"); 
                            _currentItem = currentTrack;
                            OnPlayingItemChanged?.Invoke(_currentItem);
                        }
                    }
                }
                else if (newContext.Item.Type == ItemType.Episode)
                {
                    FullEpisode currentEpisode = newContext.Item as FullEpisode;

                    // If no previous item or current item is different type
                    if (_currentItem == null || (_currentItem != null && _currentItem is FullTrack track))
                    {
                        Console.WriteLine($"No prev episode or new type | -> '{currentEpisode.Show.Publisher} {currentEpisode.Name}'");
                        _currentItem = currentEpisode;
                        OnPlayingItemChanged?.Invoke(_currentItem);
                    }
                    else if (_currentItem != null && _currentItem is FullEpisode lastEpisode)
                    {
                        if (lastEpisode.Name != currentEpisode.Name || lastEpisode.Show?.Publisher != currentEpisode.Show?.Publisher)
                        {
                            Console.WriteLine($"Episode to new Episode | '{lastEpisode.Show.Publisher} {lastEpisode.Name}' -> '{currentEpisode.Show.Publisher} {currentEpisode.Name}'");
                            _currentItem = currentEpisode;
                            OnPlayingItemChanged?.Invoke(_currentItem);
                        }
                    }
                }
            }
            else
            {
                // No context or null current playing item

                // If previous item has been set
                if (_currentItem != null)
                {
                    Console.WriteLine($"Context null | '{(_currentItem.Type == ItemType.Track ? (_currentItem as FullTrack).Name : (_currentItem as FullEpisode).Name)}' -> ?");
                    _currentItem = null;
                    OnPlayingItemChanged?.Invoke(null);
                }
            }

            _currentContext = newContext;
        }
        else
        {
            // If no client but has a previous item, invoke event
            if (_currentItem != null)
            {
                _currentItem = null;
                OnPlayingItemChanged?.Invoke(null);
            }
        }
    }

    /// <summary>
    /// Gets the current context of the spotify player
    /// </summary>
    /// <returns></returns>
    protected CurrentlyPlayingContext GetCurrentContext()
    {
        return _currentContext;
    }
}

