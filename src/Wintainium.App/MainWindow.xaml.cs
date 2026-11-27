using System.Windows;
using Wintainium.Core;

namespace Wintainium.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
    }
}
