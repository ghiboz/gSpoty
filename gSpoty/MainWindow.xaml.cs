using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace gSpoty
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        int imgSize = 100;
        int imgSizeBig = 640;
        string coverFolder = "Cover";

        public MainWindow()
        {
            int margin = 10;
            SizeChanged += (o, e) =>
            {
                var r = SystemParameters.WorkArea;
                Left = r.Right - ActualWidth - margin;
                Top =  r.Bottom - ActualHeight - margin;
            };

            InitializeComponent();
            imgSize = (int)imgMain.Width;
            var l = new SpotifyPlayerListener();
            l.OnPlayingItemChanged += L_OnPlayingItemChanged;
            l.OnSpotifyUpdate += L_OnSpotifyUpdate;
            string cfg = File.ReadAllText("spotify.json");
            var authConfig = JsonConvert.DeserializeObject<ClientCredentials_AuthConfig>(cfg);
            if (!string.IsNullOrEmpty(authConfig.CoverFolder))
            {
                coverFolder = authConfig.CoverFolder;
            }
        }

        private void L_OnSpotifyUpdate(int obj)
        {
            lblUpdate.Dispatcher.BeginInvoke(new Action(() =>
            {
                lblUpdate.Text = $"{obj}";
            }));
        }

        private void L_OnPlayingItemChanged(SpotifyAPI.Web.IPlayableItem obj)
        {
            var track = obj as SpotifyAPI.Web.FullTrack;
            if (track == null)
            {
                lblMain.Dispatcher.BeginInvoke(new Action(() =>
                {
                    lblMain.Text = "Please play this song on the radio";
                }));

                imgMain.Dispatcher.BeginInvoke(new Action(() =>
                {
                    imgMain.Source = null;
                }
                ));

                return;
            }
            var newLbl = $"{S4UUtility.GetTrackString(track)}";

            //lblMain.Dispatcher  .Text = $"{S4UUtility.GetTrackString(obj as SpotifyAPI.Web.FullTrack)}";

            lblMain.Dispatcher.BeginInvoke(new Action(() =>
            {
                lblMain.Text = newLbl;
            }));

            //var img = S4UUtility.GetLowestResolutionImage(track.Album.Images, imgSize, imgSize);
            var imgBig = S4UUtility.GetLowestResolutionImage(track.Album.Images, imgSizeBig, imgSizeBig);
            var ar = GetNameClean(track.Artists.FirstOrDefault().Name);
            var al =GetNameClean(track.Album.Name);

            if (ar.StartsWith("The "))
            {
                ar = ar.Substring(4);
            }

            imgMain.Dispatcher.BeginInvoke(new Action(() =>
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imgBig.Url, UriKind.Absolute);
                bitmap.EndInit();
                imgMain.Source = bitmap;

                var fileName = $@"{coverFolder}\{ar} - {al}.jpg";
                CheckPath(fileName);
                using (WebClient client = new WebClient())
                {
                    client.DownloadFileAsync(new Uri(imgBig.Url, UriKind.Absolute), fileName);
                }
            }
            ));
        }

        string GetNameClean(string src)
        {
            string invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(System.IO.Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            return System.Text.RegularExpressions.Regex.Replace(src, invalidRegStr, "_");
        }

        void CheckPath(string path)
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
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
    }
}
