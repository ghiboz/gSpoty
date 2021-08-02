using System;
using System.Collections.Generic;
using System.Linq;
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
            var newLbl = $"{S4UUtility.GetTrackString(track)}";
            //lblMain.Dispatcher  .Text = $"{S4UUtility.GetTrackString(obj as SpotifyAPI.Web.FullTrack)}";

            lblMain.Dispatcher.BeginInvoke(new Action(() =>
            {
                lblMain.Text = newLbl;
            }));

            var img = S4UUtility.GetLowestResolutionImage(track.Album.Images, imgSize, imgSize);

            imgMain.Dispatcher.BeginInvoke(new Action(() =>
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(img.Url, UriKind.Absolute);
                bitmap.EndInit();
                imgMain.Source = bitmap;
            }
            ));
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
