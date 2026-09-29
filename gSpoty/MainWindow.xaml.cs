using Newtonsoft.Json;
using SpotifyAPI.Web;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace gSpoty
{
    public class SimpleCommand : ICommand
    {
        public event EventHandler<object> Executed;
        public bool CanExecute(object parameter)
        {
            return true;
        }
        public void Execute(object parameter)
        {
            Executed?.Invoke(this, parameter);
        }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }


    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        const string NoTrackText = "Please play this song on the radio";
        const int imgSizeBig = 640;

        static readonly HttpClient http = new HttpClient();
        static readonly Regex invalidFileChars = new Regex(
            string.Format(@"([{0}]*\.+$)|([{0}]+)", Regex.Escape(new string(Path.GetInvalidFileNameChars()))),
            RegexOptions.Compiled);

        int BKG_ADD = 400;
        string coverFolder = "Cover";
        readonly SpotifyPlayerListener listener;

        bool inBackground = false;
        string lastError;
        public MainWindow()
        {
            string[] args = Environment.GetCommandLineArgs();
            int bkgIndex = Array.IndexOf(args, "-BACKGROUND");
            inBackground = bkgIndex >= 0;
            if (inBackground && bkgIndex + 1 < args.Length)
            {
                int.TryParse(args[bkgIndex + 1], out BKG_ADD);
            }

            int margin = 10;
            SizeChanged += (o, e) =>
            {
                var r = SystemParameters.WorkArea;
                Left = r.Right - ActualWidth - margin;
                Top = r.Bottom - ActualHeight - margin;
            };

            InitializeComponent();

            if (inBackground)
            {
                Height += BKG_ADD;
                Width += BKG_ADD;
                imgMain.Width += BKG_ADD;
                imgMain.Height += BKG_ADD;
                colImg.Width = new GridLength(colImg.Width.Value + BKG_ADD);
                Topmost = false;
            }

            string cfg = File.ReadAllText("spotify.json");
            var authConfig = JsonConvert.DeserializeObject<ClientCredentials_AuthConfig>(cfg);
            if (!string.IsNullOrEmpty(authConfig.CoverFolder))
            {
                coverFolder = authConfig.CoverFolder;
            }

            listener = new SpotifyPlayerListener(authConfig.PlayList, authConfig.PlayListNew);
            listener.OnPlayingItemChanged += Listener_OnPlayingItemChanged;
            listener.OnSpotifyUpdate += Listener_OnSpotifyUpdate;
            listener.OnSongAddedToPlayList += Listener_OnSongAddedToPlayList;
            listener.OnSongPlaying += Listener_OnSongPlaying;
            listener.OnError += Listener_OnError;
        }

        void OnUI(Action action) => Dispatcher.BeginInvoke(action);

        private void Listener_OnSongPlaying(double value)
        {
            OnUI(() =>
            {
                tbInfo.ProgressState = TaskbarItemProgressState.Normal;
                tbInfo.ProgressValue = value;
            });
        }

        private void Listener_OnSongAddedToPlayList(bool hasAdded)
        {
            OnUI(() => lblUpdate.Foreground = hasAdded ? Brushes.Lime : Brushes.Orange);
        }

        private void Listener_OnError(string message)
        {
            OnUI(() =>
            {
                lblUpdate.Foreground = Brushes.Red;
                lblUpdate.ToolTip = message;
                lastError = message;
            });
        }

        private void Listener_OnSpotifyUpdate(int obj)
        {
            OnUI(() => lblUpdate.Text = lastError == null ? $"♦{obj}♦" : $"♦{obj}♦ ⚠");
        }

        private async void Listener_OnPlayingItemChanged(IPlayableItem obj)
        {
            if (obj is not FullTrack track)
            {
                OnUI(() =>
                {
                    lblMain.Text = NoTrackText;
                    imgMain.Source = null;
                    lblUpdate.Foreground = Brushes.White;
                    tbInfo.ProgressState = TaskbarItemProgressState.None;
                });
                return;
            }

            var newLbl = S4UUtility.GetTrackString(track);
            OnUI(() =>
            {
                lblMain.Text = newLbl;
                lblUpdate.Foreground = Brushes.White;
            });

            try
            {
                await UpdateCover(track);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cover update failed: {ex.Message}");
            }
        }

        private async Task UpdateCover(FullTrack track)
        {
            var album = await listener.GetRealAlbum(track);
            if (album == null)
                return;

            var imgBig = S4UUtility.GetLowestResolutionImage(album.Images, imgSizeBig, imgSizeBig)
                // Fallback alle immagini della traccia se l'album non ne ha
                ?? S4UUtility.GetLowestResolutionImage(track.Album.Images, imgSizeBig, imgSizeBig);
            if (imgBig == null)
                return;

            var ar = GetNameClean(album.Artists.FirstOrDefault()?.Name ?? "Unknown");
            var al = GetNameClean(album.Name);
            if (ar.StartsWith("The "))
            {
                ar = ar.Substring(4);
            }

            // Download once: the same bytes feed both the UI and the cover file
            var bytes = await http.GetByteArrayAsync(imgBig.Url);
            Directory.CreateDirectory(coverFolder);
            await File.WriteAllBytesAsync(Path.Combine(coverFolder, $"{ar} - {al}.jpg"), bytes);

            // The song may have changed while we were resolving the album
            if (listener.CurrentItem != track)
                return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            OnUI(() => imgMain.Source = bitmap);

        }

        static string GetNameClean(string src)
        {
            return invalidFileChars.Replace(src, "_");
        }

        private void imgMain_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            this.Close();
        }


        bool white = true;
        private void lblMain_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            lblMain.Foreground = white ? Brushes.Black : Brushes.White;
            lblUpdate.Foreground = lblMain.Foreground;
            white = !white;
        }

        private void ClearError()
        {
            lastError = null;
            lblUpdate.ToolTip = null;
        }

        private async void AddSong()
        {
            ClearError();
            lblUpdate.Foreground = Brushes.DodgerBlue;
            await listener.AddSongToPlaylist();
        }

        private void lblUpdate_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => AddSong();

        private void DoubleClickOnImage(object sender, object e) => AddSong();

        private void Add_Click(object sender, RoutedEventArgs e) => AddSong();

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            ClearError();
            lblUpdate.Foreground = Brushes.OrangeRed;
            await listener.RemoveSongFromPlaylist();
        }
    }
}
