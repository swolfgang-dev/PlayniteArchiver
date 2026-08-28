using System.Windows;
using System.Windows.Controls;

namespace GameArchiver
{
    public partial class GameArchiverSettingsView : UserControl
    {
        private readonly GameArchiverPlugin plugin;

        public GameArchiverSettingsView(GameArchiverPlugin plugin)
        {
            this.plugin = plugin;
            InitializeComponent();
        }

        private void BrowseArchiveRoot_Click(object sender, RoutedEventArgs e)
        {
            plugin.SelectArchiveRoot();
        }
    }
}
