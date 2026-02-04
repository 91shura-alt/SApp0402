using System.Windows;
using SellerOps.App.Views;

namespace SellerOps.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => RootFrame.Navigate(new HomePage());
        }
    }
}
